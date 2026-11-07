using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MovieDuelsLauncher
{
    // A news article's HTML, shown as WPF text. Only the simple parts are used, so an article is quick to write and
    // nothing in it can run: <h2> <h3> <p> <p class="note"> <ul>/<ol> <li> <b>/<strong> <i>/<em> <a href> <br>
    // <img src>. Other tags are skipped (their text stays), <script> and <style> are dropped with their content.
    // Links open in the browser; pictures come from the news repository like the article.
    //
    // A YouTube <iframe> (the embed code YouTube's Share button gives) shows as the video's picture with a play
    // button; a click opens the video in the browser. Playing it inside the launcher would need a browser engine
    // (WebView2). Other iframes become a link to their page.
    public static class HtmlText
    {
        private static readonly Regex Token = new Regex(@"<!--.*?-->|<(/?)([a-zA-Z][a-zA-Z0-9]*)([^>]*)>|([^<]+)", RegexOptions.Singleline);
        private static readonly Regex Attr = new Regex(@"([a-zA-Z-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))");
        private static readonly Regex Space = new Regex(@"\s+");
        private static readonly Regex YouTube = new Regex(@"^https://(?:www\.|m\.)?(?:youtube(?:-nocookie)?\.com/(?:embed/|watch\?v=|shorts/)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase);

        // the id of a YouTube video address, or null
        public static string YouTubeId(string src)
        {
            Match m = YouTube.Match(src ?? "");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static string YouTubePicture(string id)
        {
            return "https://img.youtube.com/vi/" + id + "/hqdefault.jpg";
        }

        public sealed class Styles
        {
            public FontFamily Text;
            public FontFamily Heading;
            public Brush Foreground;
            public Brush HeadingBrush;
            public Brush LinkBrush;
            public Brush NoteBackground;
            public Brush NoteBorder;
        }

        // the pictures of an article (<img src>, and the picture of each YouTube video), so they can be loaded before it is shown
        public static List<string> Images(string html)
        {
            var list = new List<string>();
            foreach (Match m in Token.Matches(html ?? ""))
            {
                if (m.Groups[1].Value.Length > 0)
                {
                    continue;
                }
                string tag = m.Groups[2].Value.ToLowerInvariant();
                string src = Attribute(m.Groups[3].Value, "src");
                if (tag == "img" && src != null)
                {
                    list.Add(src);
                }
                else if (tag == "iframe" && YouTubeId(src) != null)
                {
                    list.Add(YouTubePicture(YouTubeId(src)));
                }
            }
            return list;
        }

        // a video: its picture at the size the iframe asks for (16:9, at most 640 wide), a play button, "Watch on YouTube"
        private static Block Video(string id, int width, ImageSource picture, Action<string> openLink)
        {
            width = Math.Max(240, Math.Min(640, width));
            var frame = new Grid { Width = width, Height = Math.Round(width * 9.0 / 16), HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand, ClipToBounds = true };
            frame.Clip = new RectangleGeometry(new Rect(0, 0, frame.Width, frame.Height), 10, 10);
            frame.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)) });
            if (picture != null)
            {
                frame.Children.Add(new Image { Source = picture, Stretch = Stretch.UniformToFill });
            }
            var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.55));
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 1));
            frame.Children.Add(new Border { Background = shade });
            var play = new Border
            {
                Width = 68, Height = 48, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x21, 0x21, 0x21)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L18,11 L0,22 Z"), Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) }
            };
            frame.Children.Add(play);
            frame.Children.Add(new TextBlock
            {
                Text = "Watch on YouTube \u2197", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 0, 11)
            });
            var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
            frame.MouseEnter += (s, e) => play.Background = red;
            frame.MouseLeave += (s, e) => play.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x21, 0x21, 0x21));
            frame.MouseLeftButtonDown += (s, e) => e.Handled = true; // a click, not a window drag
            frame.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                openLink("https://www.youtube.com/watch?v=" + id);
            };
            frame.ToolTip = "https://www.youtube.com/watch?v=" + id;
            return new BlockUIContainer(frame) { Margin = new Thickness(0, 4, 0, 14) };
        }

        public static void AppendTo(FlowDocument doc, string html, Styles styles, Func<string, ImageSource> image, Action<string> openLink)
        {
            Block block = null;           // the paragraph being filled
            Paragraph paragraph = null;
            var lists = new Stack<List>();
            var inlines = new Stack<Span>(); // b, i, a around the text
            string skip = null;           // inside <script> / <style>

            Paragraph NewParagraph(string kind)
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                if (kind == "h2" || kind == "h3")
                {
                    p.FontFamily = styles.Heading;
                    p.FontSize = kind == "h2" ? 24 : 19;
                    p.Foreground = styles.HeadingBrush;
                    p.Margin = new Thickness(0, 6, 0, 6);
                }
                else if (kind == "note")
                {
                    p.Background = styles.NoteBackground;
                    p.BorderBrush = styles.NoteBorder;
                    p.BorderThickness = new Thickness(1);
                    p.Padding = new Thickness(12, 8, 12, 8);
                    p.FontSize = 13;
                }
                if (lists.Count > 0)
                {
                    var item = new ListItem(p);
                    p.Margin = new Thickness(0, 0, 0, 4);
                    lists.Peek().ListItems.Add(item);
                }
                else
                {
                    doc.Blocks.Add(p);
                }
                inlines.Clear();
                block = p;
                return p;
            }

            void AddInline(Inline inline)
            {
                if (paragraph == null)
                {
                    paragraph = NewParagraph("p");
                }
                if (inlines.Count > 0)
                {
                    inlines.Peek().Inlines.Add(inline);
                }
                else
                {
                    paragraph.Inlines.Add(inline);
                }
            }

            foreach (Match m in Token.Matches(html ?? ""))
            {
                if (m.Value.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }
                bool closing = m.Groups[1].Value.Length > 0;
                string tag = m.Groups[2].Value.ToLowerInvariant();
                if (skip != null)
                {
                    if (closing && tag == skip)
                    {
                        skip = null;
                    }
                    continue;
                }
                if (m.Groups[4].Success)
                {
                    string text = Space.Replace(WebUtility.HtmlDecode(m.Groups[4].Value), " ");
                    if (paragraph == null && text.Trim().Length == 0)
                    {
                        continue; // the space between blocks
                    }
                    if (paragraph != null && paragraph.Inlines.Count == 0 && inlines.Count == 0)
                    {
                        text = text.TrimStart();
                    }
                    if (text.Length > 0)
                    {
                        AddInline(new Run(text));
                    }
                    continue;
                }
                string attrs = m.Groups[3].Value;
                switch (tag)
                {
                    case "script":
                    case "style":
                        if (!closing && !attrs.TrimEnd().EndsWith("/", StringComparison.Ordinal))
                        {
                            skip = tag;
                        }
                        break;
                    case "p":
                    case "div":
                    case "h1":
                    case "h2":
                    case "h3":
                    case "h4":
                        if (closing)
                        {
                            paragraph = null;
                        }
                        else
                        {
                            string kind = tag == "h1" ? "h2" : tag == "h4" ? "h3" : tag;
                            if (kind == "p" && (Attribute(attrs, "class") ?? "").Contains("note"))
                            {
                                kind = "note";
                            }
                            paragraph = NewParagraph(kind);
                        }
                        break;
                    case "ul":
                    case "ol":
                        if (closing)
                        {
                            if (lists.Count > 0)
                            {
                                lists.Pop();
                            }
                        }
                        else
                        {
                            var list = new List
                            {
                                MarkerStyle = tag == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                                Margin = new Thickness(0, 0, 0, 10),
                                Padding = new Thickness(22, 0, 0, 0)
                            };
                            if (lists.Count > 0 && lists.Peek().ListItems.LastListItem != null)
                            {
                                lists.Peek().ListItems.LastListItem.Blocks.Add(list);
                            }
                            else
                            {
                                doc.Blocks.Add(list);
                            }
                            lists.Push(list);
                        }
                        paragraph = null;
                        break;
                    case "li":
                        paragraph = closing ? null : NewParagraph("p");
                        break;
                    case "br":
                        AddInline(new LineBreak());
                        break;
                    case "b":
                    case "strong":
                    case "i":
                    case "em":
                    case "a":
                        if (closing)
                        {
                            if (inlines.Count > 0)
                            {
                                inlines.Pop();
                            }
                            break;
                        }
                        Span span;
                        if (tag == "a")
                        {
                            string href = Attribute(attrs, "href");
                            var link = new Hyperlink { Foreground = styles.LinkBrush, Cursor = System.Windows.Input.Cursors.Hand };
                            if (href != null && href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                link.ToolTip = href;
                                link.Click += (s, e) => openLink(href);
                            }
                            span = link;
                        }
                        else if (tag == "b" || tag == "strong")
                        {
                            span = new Bold();
                        }
                        else
                        {
                            span = new Italic();
                        }
                        AddInline(span);
                        inlines.Push(span);
                        break;
                    case "iframe":
                        if (!closing)
                        {
                            string src = Attribute(attrs, "src");
                            string id = YouTubeId(src);
                            int.TryParse(Attribute(attrs, "width"), out int width);
                            if (id != null)
                            {
                                doc.Blocks.Add(Video(id, width > 0 ? width : 480, image(YouTubePicture(id)), openLink));
                                paragraph = null;
                            }
                            else if (src != null && src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                paragraph = NewParagraph("p");
                                var link = new Hyperlink(new Run("Open the embedded page \u2197")) { Foreground = styles.LinkBrush, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = src };
                                link.Click += (s, e) => openLink(src);
                                paragraph.Inlines.Add(link);
                                paragraph = null;
                            }
                        }
                        break;
                    case "img":
                        if (!closing)
                        {
                            ImageSource source = image(Attribute(attrs, "src"));
                            if (source != null)
                            {
                                var picture = new Image { Source = source, Stretch = Stretch.Uniform, MaxHeight = 320, HorizontalAlignment = HorizontalAlignment.Left };
                                doc.Blocks.Add(new BlockUIContainer(picture) { Margin = new Thickness(0, 4, 0, 12) });
                                paragraph = null;
                            }
                        }
                        break;
                }
            }
            if (block == null && doc.Blocks.Count == 0)
            {
                doc.Blocks.Add(new Paragraph(new Run("This article is empty.")));
            }
        }

        private static string Attribute(string attrs, string name)
        {
            foreach (Match a in Attr.Matches(attrs ?? ""))
            {
                if (a.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    string v = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                    return WebUtility.HtmlDecode(v).Trim();
                }
            }
            return null;
        }
    }
}
