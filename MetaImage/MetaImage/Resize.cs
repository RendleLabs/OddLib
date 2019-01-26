using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Primitives;

namespace MetaImage
{
    public static class Resize
    {
        public static readonly HttpClient Http = new HttpClient();

        [FunctionName("meta-image")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)]
            HttpRequest req, TraceWriter log)
        {
            if (!req.Query.TryGetValue("u", out var uv)) return NotFound();
            var path = uv.FirstOrDefault();
            string html = await GetHtmlAsync(log, path);
            if (html == null) return NotFound();

            var metaImageUri = GetImageUri(path, html, log);

            if (metaImageUri == null) return NotFound();

            log.Info($"GET '{metaImageUri}'");
            var imageResponse = await Http.GetAsync(metaImageUri);

            if (!imageResponse.IsSuccessStatusCode) return NotFound();

            try
            {
                var image = Image.Load(await imageResponse.Content.ReadAsStreamAsync());

                ResizeImage(req, image);

                var stream = new MemoryStream();
                var encoder = new JpegEncoder {Quality = 60};
                image.SaveAsJpeg(stream, encoder);

                stream.Position = 0;

                var response = new HttpResponseMessage(HttpStatusCode.OK) {Content = new StreamContent(stream)};
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
                response.Headers.CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromDays(1),
                    MustRevalidate = false,
                    Private = false
                };

                return response;
            }
            catch (Exception ex)
            {
                log.Error($"Resize failed for '{metaImageUri}'", ex);
                return NotFound();
            }
        }

        private static HttpResponseMessage NotFound()
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static void ResizeImage(HttpRequest req, Image<Rgba32> image)
        {
            var size = GetSize(req, image);
            image.Mutate(context =>
            {
                context.Resize(new ResizeOptions
                    {
                        Size = size,
                        Mode = ResizeMode.Max,
                        Position = AnchorPositionMode.Center
                    })
                    .Resize(new ResizeOptions
                    {
                        Size = size,
                        Mode = ResizeMode.Crop,
                        Position = AnchorPositionMode.Center
                    });
            });
        }

        private static Size GetSize<T>(HttpRequest req, Image<T> image) where T : struct, IPixel<T>
        {
            if (!(req.Query.TryGetValue("w", out var wv) && int.TryParse(wv.FirstOrDefault() ?? "320", out var w)))
            {
                w = image.Width / 10;
            }

            if (!(req.Query.TryGetValue("h", out var hv) && int.TryParse(hv.FirstOrDefault() ?? "240", out var h)))
            {
                h = image.Height / 10;
            }

            return new Size(w, h);
        }

        private static string GetImageUri(string path, string html, TraceWriter log)
        {
            var parser = new HtmlParser();
            var document = parser.ParseDocument(html);
            var metaImage = document.QuerySelector("head > meta[property='og:image']") ??
                            document.QuerySelector("head > meta[property='twitter:image']");

            string imageUrl;

            if (!string.IsNullOrWhiteSpace(imageUrl = metaImage?.GetAttribute("content")))
            {
                return imageUrl;
            }

            log.Warning($"No image meta tag found on '{path}'");
            return null;
        }

        private static async Task<string> GetHtmlAsync(TraceWriter log, string uri)
        {
            try
            {
            log.Info($"Requesting '{uri}'");
            var response = await Http.GetAsync(uri);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }

            log.Error($"No page found for '{uri}'");
            {
                return null;
            }

            }
            catch (Exception e)
            {
                log.Error(e.Message, e);
                return null;
            }
        }
    }
}