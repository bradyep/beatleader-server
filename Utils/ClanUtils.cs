﻿using BeatLeader_Server.Migrations.ReadApp;
using BeatLeader_Server.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Numerics;

namespace BeatLeader_Server.Utils
{
    public static class ClanUtils
    {
        public struct ClanRankingData
        {
            public int numberOfScores;
            public float weight;
            public int lastUpdateTime;
            public float clanPP;
            public float totalAcc;
            public int totalRank;
            public int totalScore;
            public List<Score> Scores;
        };


        public static float RecalculateClanPP(this AppContext context, int clanId)
        {
            Clan clan = context.Clans.Where(c => c.Id == clanId).Include(c => c.Players).FirstOrDefault();
            var ranked = clan.Players.OrderByDescending(s => s.Pp).ToList();
            float resultPP = 0f;
            foreach ((int i, Player p) in ranked.Select((value, i) => (i, value)))
            {
                float weight = MathF.Pow(0.965f, i);
                resultPP += p.Pp * weight;
            }
            return resultPP;
        }

        public static ICollection<ClanRanking> CalculateClanRanking(this AppContext context, Leaderboard leaderboard)
        {
            if (leaderboard == null) 
            {
                return null;
            }

            // Calculate owning clan on this leaderboard
            var newClanRankingData = new Dictionary<Clan, ClanRankingData>();
            // Why can't I put a Where clause of: ".Where(s => s.LeaderboardId == leaderboardId && !s.Banned && s.Player.Clans != null)"
            // I want to ignore all players who aren't in any clans
            var leaderboardClans =
                context
                    .Scores
                    .Where(s => s.LeaderboardId == leaderboard.Id && !s.Banned)
                    .OrderByDescending(el => el.Pp)
                    .Include(s => s.Player)
                    .ThenInclude(p => p.Clans)
                    //.Select(s => new { Clans = s.Player.Clans, Pp = s.Pp, Acc = s.Accuracy, Rank = s.Rank, Score = s.ModifiedScore })
                    .ToList();

            // Build up a dictionary of the clans on this leaderboard based on scores
            foreach (Score score in leaderboardClans)
            {
                if (score.Player.Clans == null)
                {
                    continue;
                }
                foreach (Clan clan in score.Player.Clans)
                {
                    if (newClanRankingData.ContainsKey(clan))
                    {
                        int numberOfScores = newClanRankingData[clan].numberOfScores + 1;
                        float weight = MathF.Pow(0.965f, newClanRankingData[clan].weight);
                        int totalRank = newClanRankingData[clan].totalRank + score.Rank;
                        float clanPP = newClanRankingData[clan].clanPP + (score.Pp * weight);
                        float totalAcc = newClanRankingData[clan].clanPP + score.Accuracy;
                        int totalScore = newClanRankingData[clan].totalScore + score.ModifiedScore;
                        int lastUpdateTime = Math.Max(Int32.Parse(score.Timeset), newClanRankingData[clan].lastUpdateTime);
                        newClanRankingData[clan] = new ClanRankingData
                        {
                            numberOfScores = numberOfScores,
                            weight = weight,
                            lastUpdateTime = lastUpdateTime,
                            clanPP = clanPP,
                            totalAcc = totalAcc,
                            totalRank = totalRank,
                            totalScore = totalScore,
                        };
                        // Can we do this in the initializer somehow?
                        newClanRankingData[clan].Scores.Add(score);
                    }
                    else
                    {
                        newClanRankingData.Add(clan, new ClanRankingData
                        {
                            numberOfScores = 1,
                            weight = 1.0f,
                            lastUpdateTime = Int32.Parse(score.Timeset),
                            clanPP = score.Pp,
                            totalAcc = score.Accuracy,
                            totalRank = score.Rank,
                            totalScore = score.ModifiedScore,
                            Scores = new List<Score> { score }
                        });
                    }
                }
            }

            // Cases covered -------
            // Null -> Null : Good
            // Null -> Won : Good
            // Null -> Tied : Good
            // Won -> Null : Impossible
            // Won -> Won same Clan : Good
            // Won -> Won diff Clan : Good
            // Won -> Tied : Good
            // Tied -> Won : Good
            // Tied -> Tied : Good
            // Tied -> Null : Impossible
            // ----------------------
            if (!newClanRankingData.IsNullOrEmpty())
            {
                // Sort newClanRankingData and clanRanking by PP
                newClanRankingData = newClanRankingData.OrderBy(x => x.Value.clanPP).ToDictionary(x => x.Key, x => x.Value);
                leaderboard.ClanRanking ??= new List<ClanRanking>();
                var clanRanking = leaderboard.ClanRanking;

                if (clanRanking.Count != 0)
                {
                    clanRanking = clanRanking.OrderByDescending(cr => cr.ClanPP).ToList();
                    var prevCaptor = clanRanking.First().Clan;

                    // If we are introducing a tie, remove captor from the clanRanking, if there is one
                    if (newClanRankingData.Count > 1 && newClanRankingData.ElementAt(0).Value.clanPP == newClanRankingData.ElementAt(1).Value.clanPP)
                    {
                        // The reason this has to be in a loop is because of n-way ties.
                        // If an n-way tie existed in the clanRanking, the previous captor could be anywhere in the clanRanking list.
                        foreach (var clan in clanRanking)
                        {
                            RemoveCapturedLeaderboard(ref prevCaptor, leaderboard);
                        }
                        leaderboard.ClanRankingContested = true;
                    }
                    else
                    {
                        // If the leaderboard was previously tied, and now it is captured, we don't want to remove captor (there wasn't one)
                        if (clanRanking.Count > 1 && leaderboard.ClanRankingContested)
                        {
                            AddCapturedLeaderboard(ref newClanRankingData, leaderboard);
                            leaderboard.ClanRankingContested = false;
                        } 
                        else
                        {
                            // If the leaderboard was previously won, and now it is won by a different clan,
                            // Remove board from old captor, add board to new captor
                            if (prevCaptor != newClanRankingData.First().Key)
                            {
                                RemoveCapturedLeaderboard(ref prevCaptor, leaderboard);
                                AddCapturedLeaderboard(ref newClanRankingData, leaderboard);
                            }
                        }
                    }
                } 
                else
                {
                    // If clanRanking was empty:
                    // Empty --> Tie : Set leaderboard as contested
                    if (newClanRankingData.Count > 1 && newClanRankingData.ElementAt(0).Value.clanPP == newClanRankingData.ElementAt(1).Value.clanPP) {
                        leaderboard.ClanRankingContested = true;
                    } 
                    else
                    {
                        // Empty --> Won : Add captured leaderboard
                        AddCapturedLeaderboard(ref newClanRankingData, leaderboard);
                    }
                }

                // Recalculate pp on clans
                // TODO : Maybe have function that only needs to do these operations for the clans affected by a new score set?
                int clanRankCounter = 1;
                foreach (var clan in newClanRankingData)
                {
                    var updateClan = clanRanking.Where(cr => cr.Clan == clan.Key).FirstOrDefault();
                    if (updateClan != null)
                    {
                        updateClan.LastUpdateTime = clan.Value.lastUpdateTime.ToString();
                        updateClan.ClanPP = clan.Value.clanPP;
                        updateClan.ClanRank = clanRankCounter;
                        updateClan.ClanAverageRank = clan.Value.totalRank / clan.Value.numberOfScores;
                        updateClan.ClanAverageAccuracy = clan.Value.totalAcc / clan.Value.numberOfScores;
                        updateClan.ClanTotalScore = clan.Value.totalScore;
                        updateClan.AssociatedScores = clan.Value.Scores;
                    }
                    else
                    {
                        clanRanking.Add(new ClanRanking { 
                            Clan = clan.Key,
                            LastUpdateTime = clan.Value.lastUpdateTime.ToString(),
                            ClanPP = clan.Value.clanPP,
                            ClanRank = clanRankCounter,
                            ClanAverageRank = clan.Value.totalRank / clan.Value.numberOfScores,
                            ClanAverageAccuracy = clan.Value.totalAcc / clan.Value.numberOfScores,
                            ClanTotalScore = clan.Value.totalScore,
                            LeaderboardId = leaderboard.Id,
                            Leaderboard = leaderboard,
                            AssociatedScores = clan.Value.Scores
                        });
                    }
                    clanRankCounter++;
                }
            }

            return leaderboard.ClanRanking;
        }

        private static void AddCapturedLeaderboard(ref Dictionary<Clan, ClanRankingData> newClanRankingData, Leaderboard leaderboard)
        {
            // Add leaderboard to new captor
            if (newClanRankingData.First().Key.CapturedLeaderboards == null)
            {
                newClanRankingData.First().Key.CapturedLeaderboards = new List<Leaderboard>
                {
                    leaderboard
                };
            }
            else
            {
                // Check to make sure the clan hasn't already captured this map
                if (!newClanRankingData.First().Key.CapturedLeaderboards.Contains(leaderboard))
                {
                    newClanRankingData.First().Key.CapturedLeaderboards.Add(leaderboard);
                }
            }
        }

        private static void RemoveCapturedLeaderboard(ref Clan prevCaptor, Leaderboard leaderboard)
        {
            // Remove leaderboard from old captor
            if (prevCaptor.CapturedLeaderboards != null && prevCaptor.CapturedLeaderboards.Contains(leaderboard))
            {
                prevCaptor.CapturedLeaderboards.Remove(leaderboard);
            }
        }
    }
}
