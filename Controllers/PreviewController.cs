﻿using Azure.Identity;
using Azure.Storage.Blobs;
using BeatLeader_Server.Models;
using BeatLeader_Server.Utils;
using Lib.AspNetCore.ServerTiming;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Dynamic;
using System.Net;

namespace BeatLeader_Server.Controllers
{
    public class PreviewController : Controller
    {
        private readonly HttpClient _client;
        private readonly AppContext _context;
        private readonly IWebHostEnvironment _webHostEnvironment;
        BlobContainerClient _previewContainerClient;
        private readonly IServerTiming _serverTiming;

        private Image StarImage;
        private Image AvatarMask;
        private Image AvatarShadow;
        private Image BackgroundImage;
        private Image GradientMask; 
        private Image CoverMask;
        private Image FinalMask;
        private Image GradientMaskBlurred;
        private FontFamily FontFamily;
        private EmbedGenerator EmbedGenerator;

        public PreviewController(
            AppContext context, 
            IWebHostEnvironment webHostEnvironment, 
            IOptions<AzureStorageConfig> config,
            IServerTiming serverTiming) {
            _client = new HttpClient();
            _context = context;
            _webHostEnvironment = webHostEnvironment;
            _serverTiming = serverTiming;

            if (webHostEnvironment.IsDevelopment())
            {
                _previewContainerClient = new BlobContainerClient(config.Value.AccountName, config.Value.AssetsContainerName);
            }
            else
            {
                string containerEndpoint = string.Format("https://{0}.blob.core.windows.net/{1}",
                                                        config.Value.AccountName,
                                                       config.Value.AssetsContainerName);

                _previewContainerClient = new BlobContainerClient(new Uri(containerEndpoint), new DefaultAzureCredential());
            }

            StarImage = LoadImage("Star.png");
            AvatarMask = LoadImage("AvatarMask.png");
            AvatarShadow = LoadImage("AvatarShadow.png");
            BackgroundImage = LoadImage("Background.png");
            GradientMask = LoadImage("GradientMask.png");
            CoverMask = LoadImage("CoverMask.png");
            FinalMask = LoadImage("FinalMask.png");
            GradientMaskBlurred = LoadImage("GradientMaskBlurred.png");

            var fontCollection = new PrivateFontCollection();
            fontCollection.AddFontFile(Path.Combine(_webHostEnvironment.WebRootPath + "/fonts/Teko-SemiBold.ttf"));
            FontFamily = fontCollection.Families[0];

            EmbedGenerator = new EmbedGenerator(
                new Size(500, 300),
                StarImage,
                AvatarMask,
                AvatarShadow,
                BackgroundImage,
                GradientMask,
                GradientMaskBlurred,
                CoverMask,
                FinalMask,
                FontFamily
            );
        }
        class SongSelect {
            public string Id { get; set; }
            public string CoverImage { get; set; }
            public string Name { get; set; }
            public string Hash { get; set; }
        }

        private Image LoadImage(string fileName)
        {
            return new Bitmap(_webHostEnvironment.WebRootPath + "/images/" + fileName);
        }

        private async Task<Image?> LoadOverlayImage(Player player)
        {
            ResponseUtils.PostProcessSettings(player.Role, player.ProfileSettings, player.PatreonFeatures);
            if (player.ProfileSettings?.EffectName == null || player.ProfileSettings.EffectName.Length == 0) return null;

            BlobClient blobClient = _previewContainerClient.GetBlobClient(player.ProfileSettings.EffectName + "_Effect.png");

            if (await blobClient.ExistsAsync())
            {
                return new Bitmap(await blobClient.OpenReadAsync());
            } 
            return null;
        }

        [HttpGet("~/preview/replay")]
        public async Task<ActionResult> Get(
            [FromQuery] string? playerID = null, 
            [FromQuery] string? id = null, 
            [FromQuery] string? difficulty = null, 
            [FromQuery] string? mode = null,
            [FromQuery] int? scoreId = null) {

            if (scoreId != null)
            {
                BlobClient blobClient = _previewContainerClient.GetBlobClient(scoreId + "-preview.png");
                if (await blobClient.ExistsAsync())
                {
                    return File(await blobClient.OpenReadAsync(), "image/png");
                }
            }
            else {
                return await GetOld(playerID, id, difficulty, mode);
            }

            if (!OperatingSystem.IsWindows())
            {
                return Redirect("https://freedm.azurewebsites.net/preview/replay" + Request.QueryString);
            }

            Player? player = null;
            SongSelect? song = null;
            Score? score = null;

            using (_serverTiming.TimeAction("db"))
            {
                if (scoreId != null) {
                    score = _context.Scores
                        .Where(s => s.Id == scoreId)
                        .Include(s => s.Player)
                            .ThenInclude(p => p.ProfileSettings)
                        .Include(s => s.Leaderboard)
                            .ThenInclude(l => l.Song)
                        .Include(s => s.Leaderboard)
                            .ThenInclude(l => l.Difficulty)
                        .FirstOrDefault();
                    if (score != null) {
                        player = score.Player;
                        song = new SongSelect { Id = score.Leaderboard.Song.Id, CoverImage = score.Leaderboard.Song.CoverImage, Name = score.Leaderboard.Song.Name };
                    } else {
                        var redirect = _context.ScoreRedirects.FirstOrDefault(sr => sr.OldScoreId == scoreId);
                        if (redirect != null && redirect.NewScoreId != scoreId)
                        {
                            return await Get(scoreId: redirect.NewScoreId);
                        }
                    }
                }
            }

            if (player == null || song == null)
            {
                return NotFound();
            }

            Bitmap avatarImage;
            Bitmap coverImage;

            using (_serverTiming.TimeAction("pfpAndCover"))
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, player.Avatar))
                {
                    Stream contentStream = await (await _client.SendAsync(request)).Content.ReadAsStreamAsync();
                    avatarImage = new Bitmap(contentStream);
                }

                using (var request = new HttpRequestMessage(HttpMethod.Get, song.CoverImage))
                {
                    Stream contentStream = await (await _client.SendAsync(request)).Content.ReadAsStreamAsync();
                    coverImage = new Bitmap(contentStream);
                }
            }

            Image? overlayImage = null;
            using (_serverTiming.TimeAction("overlay"))
            {
                overlayImage = await LoadOverlayImage(player);
            }

            (Color diffColor, string diff) = DiffColorAndName(score?.Leaderboard.Difficulty.DifficultyName);

            Image? result = null;
            
            using (_serverTiming.TimeAction("generate"))
            {
                result = EmbedGenerator.Generate(
                    _serverTiming,
                    player.Name,
                    song.Name,
                    (score?.FullCombo == true ? "FC" : "") + (score?.Modifiers.Length > 0 ? (score.FullCombo ? ", " : "") + String.Join(", ", score.Modifiers.Split(",")) : ""),
                    diff,
                    score?.Accuracy,
                    score?.Rank,
                    score?.Pp,
                    score?.Leaderboard.Difficulty.Stars,
                    coverImage,
                    avatarImage,
                    overlayImage,
                    player.ProfileSettings?.Hue,
                    player.ProfileSettings?.Saturation,
                    player.ProfileSettings?.RightSaberColor != null ? ColorTranslator.FromHtml(player.ProfileSettings.RightSaberColor) : Color.Red,
                    player.ProfileSettings?.LeftSaberColor != null ? ColorTranslator.FromHtml(player.ProfileSettings.LeftSaberColor) : Color.Blue,
                    diffColor
                );
            }
            
            MemoryStream ms = new MemoryStream();
            result.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            if (scoreId != null)
            {
                HttpContext.Response.OnCompleted(async () => {
                    await _previewContainerClient.DeleteBlobIfExistsAsync(scoreId + "-preview.png");
                    await _previewContainerClient.UploadBlobAsync(scoreId + "-preview.png", ms);
                });
                
            }
            ms.Position = 0;
            return File(ms, "image/png");
        }

        [NonAction]
        public async Task<ActionResult> GetOld(
            [FromQuery] string? playerID = null,
            [FromQuery] string? id = null,
            [FromQuery] string? difficulty = null,
            [FromQuery] string? mode = null)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Redirect("https://freedm.azurewebsites.net/preview/replay" + Request.QueryString);
            }

            Player? player = null;
            SongSelect? song = null;

            if (playerID != null && id != null)
            {
                player = _context.Players.Where(p => p.Id == playerID).FirstOrDefault() ?? await GetPlayerFromSS("https://scoresaber.com/api/player/" + playerID + "/full");
                song = _context.Songs.Select(s => new SongSelect { Id = s.Id, CoverImage = s.CoverImage, Name = s.Name }).Where(s => s.Id == id).FirstOrDefault();
            }

            if (player == null || song == null)
            {
                return NotFound();
            }

            int width = 500; int height = 300;
            Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            Graphics graphics = Graphics.FromImage(bitmap);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawImage(new Bitmap(_webHostEnvironment.WebRootPath + "/images/backgroundold.png"), new Rectangle(0, 0, width, height));
            graphics.DrawImage(new Bitmap(_webHostEnvironment.WebRootPath + "/images/replays.png"), new Rectangle(width / 2 - 50, height / 2 - 50 - 25, 100, 100));

            using (var request = new HttpRequestMessage(HttpMethod.Get, player.Avatar))
            {
                Stream contentStream = await (await _client.SendAsync(request)).Content.ReadAsStreamAsync();
                Bitmap avatarBitmap = new Bitmap(contentStream);

                graphics.DrawImage(avatarBitmap, new Rectangle(50, 50, 135, 135));
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, song.CoverImage))
            {
                Stream contentStream = await (await _client.SendAsync(request)).Content.ReadAsStreamAsync();
                Bitmap coverBitmap = new Bitmap(contentStream);

                graphics.DrawImage(coverBitmap, new Rectangle(width - 185, 50, 135, 135));
            }

            PrivateFontCollection fontCollection = new PrivateFontCollection();
            fontCollection.AddFontFile(_webHostEnvironment.WebRootPath + "/fonts/Audiowide-Regular.ttf");
            var fontFamily = fontCollection.Families[0];

            StringFormat stringFormat = new StringFormat();
            stringFormat.Alignment = StringAlignment.Center;

            Color nameColor = ColorFromHSV((Math.Max(0, player.Pp - 1000) / 18000) * 360, 1.0, 1.0);
            var nameFont = new Font(fontFamily, 26 - (player.Name.Length / 5) - (player.Name.Length > 15 ? 3 : 0));
            graphics.DrawString(player.Name, nameFont, new SolidBrush(nameColor), 30, new RectangleF(0, 176, width, 40), stringFormat);

            var songNameFont = new Font(fontFamily, 30 - song.Name.Length / 5);
            graphics.DrawString(song.Name, songNameFont, new SolidBrush(Color.White), 30, new RectangleF(0, 215, width, 80), stringFormat);
            graphics.DrawRectangle(new Pen(new LinearGradientBrush(new Point(1, 1), new Point(100, 100), Color.Red, Color.BlueViolet), 5), new Rectangle(0, 0, width, height));

            MemoryStream ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            return File(ms, "image/png");
        }

        [HttpGet("~/preview/royale")]
        public async Task<ActionResult> GetRoyale(
            [FromQuery] string? players = null,
            [FromQuery] string? hash = null,
            [FromQuery] string? difficulty = null,
            [FromQuery] string? mode = null)
        {

            List<Player> playersList = new List<Player>();
            SongSelect? song = null;

            if (players != null && hash != null)
            {
                var ids = players.Split(",");
                playersList = _context.Players.Where(p => ids.Contains(p.Id)).ToList();
                foreach (var id in ids)
                {
                    if (playersList.FirstOrDefault(p => p.Id == id) == null) {
                        Player? ssplayer = await GetPlayerFromSS("https://scoresaber.com/api/player/" + id + "/full");
                        if (ssplayer != null) {
                            playersList.Add(ssplayer);
                        }
                    }
                }
                    
                
                song = _context.Songs.Select(s => new SongSelect { Hash = s.Hash, CoverImage = s.CoverImage, Name = s.Name }).Where(s => s.Hash == hash).FirstOrDefault();
            }

            if (playersList.Count == 0 || song == null)
            {
                return NotFound();
            }

            int width = 500; int height = 300;
            Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            Graphics graphics = Graphics.FromImage(bitmap);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawImage(new Bitmap(_webHostEnvironment.WebRootPath + "/images/backgroundold.png"), new Rectangle(0, 0, width, height));
            graphics.DrawImage(new Bitmap(_webHostEnvironment.WebRootPath + "/images/royale.png"), new Rectangle(width - 120, 20, 100, 100));

            PrivateFontCollection fontCollection = new PrivateFontCollection();
            fontCollection.AddFontFile(_webHostEnvironment.WebRootPath + "/fonts/Audiowide-Regular.ttf");
            var fontFamily = fontCollection.Families[0];

            StringFormat left = new StringFormat();
            left.Alignment = StringAlignment.Near;

            StringFormat center = new StringFormat();
            center.Alignment = StringAlignment.Center;
            graphics.DrawString("BS", new Font(fontFamily, 9), new SolidBrush(Color.FromArgb(233, 2, 141)), new RectangleF(width - 120, 12, 100, 20), center);
            graphics.DrawString("Battle royale!", new Font(fontFamily, 9), new SolidBrush(Color.FromArgb(233, 2, 141)), new RectangleF(width - 120, 106, 100, 20), center);

            for (int i = 0; i < playersList.Count; i++)
            {
                var player = playersList[i];

                using (var request = new HttpRequestMessage(HttpMethod.Get, player.Avatar))
                {
                    Stream contentStream = await (await _client.SendAsync(request)).Content.ReadAsStreamAsync();
                    Bitmap avatarBitmap = new Bitmap(contentStream);

                    graphics.DrawImage(avatarBitmap, new Rectangle(18, 15 + i * 27, 20, 20));
                }

                var nameFont = new Font(fontFamily, 14 - (player.Name.Length / 5) - (player.Name.Length > 15 ? 3 : 0));
                graphics.DrawString(player.Name, nameFont, new SolidBrush(Color.White), new RectangleF(40, 15 + i * 27, width / 2, 40));
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, song.CoverImage))
            {
                Stream contentStream = await (await _client.SendAsync(request)).Content.ReadAsStreamAsync();
                Bitmap coverBitmap = new Bitmap(contentStream);

                graphics.DrawImage(coverBitmap, new Rectangle(width / 2 - 40, 15, 135, 135));
            }

            if (difficulty != null) {
                (Color color, string diff) = DiffColorAndName(difficulty);
                graphics.DrawString(diff, new Font(fontFamily, 12), new SolidBrush(color), new Point((int)(width / 2 - 44), 160), left);
            }

            var songNameFont = new Font(fontFamily, 24 - song.Name.Length / 5);
            graphics.DrawString(song.Name, songNameFont, new SolidBrush(Color.FromArgb(250, 42, 125)), 30, new RectangleF(width / 2 - 46, 170, width / 2 + 60, 160), left);

            graphics.DrawRectangle(new Pen(new LinearGradientBrush(new Point(1, 1), new Point(100, 100), Color.Red, Color.BlueViolet), 5), new Rectangle(0, 0, width, height));

            using (MemoryStream ms = new MemoryStream())
            {
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return File(ms.ToArray(), "image/png");
            }
        }

        private Task<Player?> GetPlayerFromSS(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Proxy = null;

            WebResponse? response = null;
            Player? song = null;
            var stream = 
            Task<(WebResponse?, Player?)>.Factory.FromAsync(request.BeginGetResponse, result =>
            {
                try
                {
                    response = request.EndGetResponse(result);
                }
                catch (Exception e)
                {
                    song = null;
                }
            
                return (response, song);
            }, request);

            return stream.ContinueWith(t => ReadPlayerFromResponse(t.Result));
        }

        private Player? ReadPlayerFromResponse((WebResponse?, Player?) response)
        {
            if (response.Item1 != null) {
                using (Stream responseStream = response.Item1.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream))
                {
                    string results = reader.ReadToEnd();
                    if (string.IsNullOrEmpty(results))
                    {
                        return null;
                    }

                    dynamic? info = JsonConvert.DeserializeObject<ExpandoObject>(results, new ExpandoObjectConverter());
                    if (info == null) return null;

                    Player result = new Player();
                    result.Name = info.name;
                    result.Avatar = info.profilePicture;
                    result.Country = info.country;
                    result.Pp = (int)info.pp;

                    return result;
                }
            } else {
                return response.Item2;
            }
            
        }

        public static Color ColorFromHSV(double hue, double saturation, double value)
        {
            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            value = value * 255;
            int v = Convert.ToInt32(value);
            int p = Convert.ToInt32(value * (1 - saturation));
            int q = Convert.ToInt32(value * (1 - f * saturation));
            int t = Convert.ToInt32(value * (1 - (1 - f) * saturation));

            if (hi == 0)
                return Color.FromArgb(255, v, t, p);
            else if (hi == 1)
                return Color.FromArgb(255, q, v, p);
            else if (hi == 2)
                return Color.FromArgb(255, p, v, t);
            else if (hi == 3)
                return Color.FromArgb(255, p, q, v);
            else if (hi == 4)
                return Color.FromArgb(255, t, p, v);
            else
                return Color.FromArgb(255, v, p, q);
        }

        public static (Color, string) DiffColorAndName(string diff) {
            switch (diff)
            {
                case "Easy": return (Color.FromArgb(16, 232, 113), "Easy");
                case "Normal": return (Color.FromArgb(99, 180, 242), "Normal");
                case "Hard": return (Color.FromArgb(255, 123, 99), "Hard");
                case "Expert": return (Color.FromArgb(227, 54, 172), "Expert");
                case "ExpertPlus": return (Color.FromArgb(180, 110, 255), "Expert+");
            }
            return (Color.White, diff);
        }
    }

    public static class GraphicsExtension
    {
        public static IEnumerable<string> GetWrappedLines(this Graphics that, string text, Font font, double maxWidth = double.PositiveInfinity)
        {
            if (String.IsNullOrEmpty(text)) return new string[0];
            if (font == null) throw new ArgumentNullException("font", "The 'font' parameter must not be null");
            if (maxWidth <= 0) throw new ArgumentOutOfRangeException("maxWidth", "Maximum width must be greater than zero");

            // See https://stackoverflow.com/questions/6111298/best-way-to-specify-whitespace-in-a-string-split-operation
            string[] words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 0) return new string[0];

            List<string> lines = new List<string>();

            float spaceCharWidth = that.MeasureString(" ", font).Width;
            float currentWidth = 0;
            string currentLine = "";
            for (int i = 0; i < words.Length; i++)
            {
                float currentWordWidth = that.MeasureString(words[i], font).Width;
                if (currentWidth != 0)
                {
                    float potentialWordWidth = spaceCharWidth + currentWordWidth;
                    if (currentWidth + potentialWordWidth < maxWidth)
                    {
                        currentWidth += potentialWordWidth;
                        currentLine += " " + words[i];
                    }
                    else
                    {
                        lines.Add(currentLine);
                        currentLine = words[i];
                        currentWidth = currentWordWidth;
                    }
                }
                else
                {
                    currentWidth += currentWordWidth;
                    currentLine = words[i];
                }

                if (i == words.Length - 1)
                {
                    lines.Add(currentLine);
                }
            }

            return lines;
        }

        public static void DrawString(this Graphics that, string text, Font font, Brush brush,
                                            int lineHeight, RectangleF layoutRectangle, StringFormat format)
        {
            string[] lines = that.GetWrappedLines(text, font, layoutRectangle.Width).ToArray();
            Rectangle lastDrawn = new Rectangle(Convert.ToInt32(layoutRectangle.X), Convert.ToInt32(layoutRectangle.Y), 0, 0);
            foreach (string line in lines)
            {
                SizeF lineSize = that.MeasureString(line, font);
                float increment = lastDrawn.Height == 0 ? (lines.Count() == 1 ? lineHeight * 0.5f : 0) : lineHeight;
                RectangleF lineOrigin = new RectangleF(lastDrawn.X, lastDrawn.Y + increment, layoutRectangle.Width, layoutRectangle.Height);
                that.DrawString(line, font, brush, lineOrigin, format);
                lastDrawn = new Rectangle(Point.Round(lineOrigin.Location), Size.Round(lineSize));
            }
        }
    }
}
