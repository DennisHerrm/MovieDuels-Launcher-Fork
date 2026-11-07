using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Resources;
using System.Windows.Threading;

namespace MovieDuelsLauncher
{
    // The launcher window (layout and looks: MainWindow.xaml).
    //
    // - The top bars open the pages: PLAY (the art, SINGLEPLAYER / MULTIPLAYER and the emblem), NEWS (the team's
    //   articles), ABOUT (links, the mod, FAQ, team, all credits) and SETTINGS (the game's options, the launcher's,
    //   the installation).
    // - SINGLEPLAYER starts MovieDuels-SP.x86_64.exe from the launcher's folder, passing the options as "+set" values,
    //   and closes the launcher. MULTIPLAYER opens the multiplayer card: Start multiplayer starts
    //   MovieDuels-MP.x86_64.exe the same way, Start server runs "Start MovieDuels-MP-FFA-Server.bat" from the MD folder
    //   in its own console (the launcher stays open, so the game can be started next to join it). Multiplayer needs
    //   Update 9: before that the button says it is coming soon.
    // - Updates: the launcher downloads nothing. It reads latest.json from MovieDuels-Launcher-News (Online); when that
    //   update is newer than the installed one (MdUpdate), the emblem glows red and its window opens ModDB or Nexus
    //   Mods. When the launcher then sees a newer update installed than last time, it checks the new files once
    //   (FilesCheck) and the bars either side of the emblem fill up; SINGLEPLAYER and MULTIPLAYER come back when
    //   they are full.
    // - The social icons open web pages in the browser (each button's Tag in the XAML).
    public partial class MainWindow : Window
    {
        private const string SPExe = "MovieDuels-SP.x86_64.exe";
        private const string MPExe = "MovieDuels-MP.x86_64.exe";

        // the mod folder (fs_game) next to the exes, and the dedicated server batch file in it (it does "cd .." itself, so
        // it runs from the mod folder)
        private const string ModFolder = "MD";

        private const string ServerBat = "Start MovieDuels-MP-FFA-Server.bat";

        // Release builds of the game only start when this is set on their process (shared/sys/sys_main.cpp
        // Sys_RequireLauncher: MD_LAUNCHER_ENV / MD_LAUNCHER_TOKEN); keep the two the same.
        private const string LauncherEnv = "MOVIEDUELS_LAUNCHER";

        private const string LauncherToken = "md-launcher-7f3a9c21";

        private const double MusicVolume = 0.6;
        private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(180); // the glow layers
        private static readonly TimeSpan PageTime = TimeSpan.FromMilliseconds(240);

        private const double BarLeft = 132, BarLeftEnd = 505, BarRight = 583, BarRightEnd = 956; // the bars either side of the emblem
        private const double CardStep = 566; // a news card and the gap after it

        private readonly string folder; // where the launcher (and the game exes) are
        private readonly LauncherSettings settings;
        private readonly int mdUpdate;  // the MovieDuels update of the game's assets (8, 9, ...)

        // multiplayer is played with Update 9 and later
        private bool HasMultiplayer => mdUpdate >= 9;

        // menu sounds (the in-game menu's own); a newer one cuts off the one still playing
        private readonly SoundPlayer clickSound;

        private readonly SoundPlayer hoverSound;
        private readonly SoundPlayer backSound;

        // background music: MediaPlayer can only play a file, so the MP3 is written to %TEMP% first
        private MediaPlayer music;

        private string musicFile;

        private enum Page { Play, News, Article, About, Settings, Multiplayer }

        private enum EmblemState { Idle, Checking, Available, Verify, Ready }

        private Page page = Page.Play;
        private EmblemState emblem = EmblemState.Idle;
        private Online.Latest latest;        // what latest.json says, or null
        private bool checkingFiles;          // the bars are running
        private FilesCheck.Result lastCheck; // the last installation check, for SETTINGS

        public MainWindow()
        {
            folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            settings = new LauncherSettings(folder);
            mdUpdate = MdUpdate.Detect(folder);
            Online.CacheFolder = Path.Combine(settings.DataFolder, "LauncherNews");

            InitializeComponent();

            BuildText.Text = AutoVersion.VersionString;

            clickSound = LoadSound("Assets/Audio/button1.wav");
            hoverSound = LoadSound("Assets/Audio/menuroam.wav");
            backSound = LoadSound("Assets/Audio/esc.wav");

            ShowOptions();
            ShowMusicButton();
            BuildAbout();
            ShowInstallation();
            ShowUpdate();
            SetPage(Page.Play, true);
            Loaded += (sender, e) =>
            {
                StartMusic();
                StartUp();
            };
        }

        // what the launcher does once it is open: check the files of a newly installed update, look for an update and for news
        private void StartUp()
        {
            if (settings.LastUpdate > 0 && mdUpdate > settings.LastUpdate)
            {
                CheckNewFiles();
            }
            else if (settings.LastUpdate != mdUpdate)
            {
                settings.LastUpdate = mdUpdate; // the first run: nothing to compare with
                settings.Save();
            }
            if (settings.CheckUpdates)
            {
                LookForUpdate(false);
            }
            LoadNews();
        }

        // ------------------------------------------------------------------------------------------
        // Sound
        // ------------------------------------------------------------------------------------------

        private static Stream OpenResource(string path)
        {
            StreamResourceInfo info = Application.GetResourceStream(new Uri(path, UriKind.Relative));
            return info?.Stream;
        }

        private static string ReadResourceText(string path)
        {
            try
            {
                using (var reader = new StreamReader(OpenResource(path)))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static SoundPlayer LoadSound(string path)
        {
            try
            {
                var player = new SoundPlayer(OpenResource(path));
                player.Load();
                return player;
            }
            catch (Exception)
            {
                return null; // no sound device or a broken file: just no sound
            }
        }

        // wait: play it to the end first (for a click right before the launcher closes)
        private static void PlaySfx(SoundPlayer sound, bool wait = false)
        {
            try
            {
                if (wait)
                {
                    sound?.PlaySync();
                }
                else
                {
                    sound?.Play();
                }
            }
            catch (Exception)
            {
            }
        }

        private void StartMusic()
        {
            if (music != null || !settings.Music)
            {
                return;
            }
            try
            {
                if (musicFile == null)
                {
                    string file = Path.Combine(Path.GetTempPath(), "MovieDuels-Launcher-music.mp3");
                    using (Stream source = OpenResource("Assets/Audio/Music.mp3"))
                    {
                        // reuse the file of an earlier run (or of a second launcher that is playing it)
                        if (!File.Exists(file) || new FileInfo(file).Length != source.Length)
                        {
                            using (FileStream target = File.Create(file))
                            {
                                source.CopyTo(target);
                            }
                        }
                    }
                    musicFile = file;
                }
                music = new MediaPlayer { Volume = MusicVolume };
                music.MediaEnded += (sender, e) =>
                {
                    // the music reached its end: play it again (loop)
                    music.Position = TimeSpan.Zero;
                    music.Play();
                };
                music.Open(new Uri(musicFile));
                music.Play();
            }
            catch (Exception)
            {
                music = null; // no MP3 playback on this PC: just no music
            }
        }

        private void StopMusic()
        {
            if (music != null)
            {
                music.Stop();
                music.Close();
                music = null;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Pages
        // ------------------------------------------------------------------------------------------

        private void SetPage(Page next, bool quiet = false)
        {
            if (!quiet)
            {
                PlaySfx(next == Page.Play || (next == Page.News && page == Page.Article) ? backSound : clickSound);
            }
            page = next;
            HideSheet();
            ShowPage(NewsPage, next == Page.News);
            ShowPage(ArticlePage, next == Page.Article);
            ShowPage(AboutPage, next == Page.About);
            ShowPage(SettingsPage, next == Page.Settings);
            ShowPage(MultiplayerPage, next == Page.Multiplayer);

            NavPlay.Tag = next == Page.Play || next == Page.Multiplayer ? "on" : null;
            NavNews.Tag = next == Page.News || next == Page.Article ? "on" : null;
            NavAbout.Tag = next == Page.About ? "on" : null;
            NavSettings.Tag = next == Page.Settings ? "on" : null;

            // the art is the PLAY page; the other pages dim it and leave the bottom bar out
            bool play = next == Page.Play;
            Art.BeginAnimation(OpacityProperty, new DoubleAnimation(play ? 1 : 0.14, TimeSpan.FromMilliseconds(300)));
            Fade(PlayBar, play, PageTime);
            Fade(PlayControls, play, PageTime);
            PlayControls.IsHitTestVisible = play;
            UpdateGlow();

            if (next == Page.News || next == Page.Article)
            {
                MarkNewsRead();
            }
            if (next == Page.Settings)
            {
                ShowInstallation();
            }
            ShowUpdate();
        }

        // a page fades in and moves up a little; a page that goes is gone at once
        private static void ShowPage(FrameworkElement element, bool show)
        {
            if (!show)
            {
                element.Visibility = Visibility.Collapsed;
                return;
            }
            if (element.Visibility == Visibility.Visible)
            {
                return;
            }
            element.Visibility = Visibility.Visible;
            var move = new TranslateTransform(0, 10);
            element.RenderTransform = move;
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, PageTime));
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, PageTime) { EasingFunction = new QuadraticEase() });
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            Page next = sender == NavNews ? Page.News : sender == NavAbout ? Page.About : sender == NavSettings ? Page.Settings : Page.Play;
            if (next != page)
            {
                SetPage(next);
            }
        }

        private int toastId;

        private void ShowToast(string text)
        {
            int id = ++toastId;
            ToastText.Text = text;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.4) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (id == toastId)
                {
                    Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
                }
            };
            timer.Start();
        }

        private static void After(double seconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }

        // ------------------------------------------------------------------------------------------
        // Updates: the emblem, its window, the bars
        // ------------------------------------------------------------------------------------------

        private bool UpdateAvailable => latest != null && latest.Update > mdUpdate && settings.CheckUpdates && !checkingFiles;

        private bool lookingForUpdate;

        private async void LookForUpdate(bool asked)
        {
            if (lookingForUpdate)
            {
                return;
            }
            lookingForUpdate = true;
            // the gold ring turns while it looks, unless the bars are running (then the emblem is theirs)
            bool ring = !checkingFiles && emblem != EmblemState.Ready;
            if (ring)
            {
                SetEmblem(EmblemState.Checking);
            }
            var started = DateTime.Now;
            string json = await Task.Run(() => Online.GetText("latest.json"));
            Online.Latest found = Online.ParseLatest(json);
            double left = 0.8 - (DateTime.Now - started).TotalSeconds; // the ring shows long enough to be seen
            if (ring && left > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(left));
            }
            if (found != null)
            {
                latest = found;
            }
            lookingForUpdate = false;
            if (emblem == EmblemState.Checking)
            {
                emblem = EmblemState.Idle;
            }
            ShowUpdate();
            if (UpdateAvailable)
            {
                PlaySfx(hoverSound);
            }
            else if (asked)
            {
                ShowToast(found == null ? "Failed to check for updates." : "Update " + mdUpdate + " is the latest. You're up to date.");
            }
        }

        // the emblem, the reminder above it, the corner text and the dot on PLAY, from the update state
        private void ShowUpdate()
        {
            if (emblem != EmblemState.Checking && emblem != EmblemState.Ready)
            {
                SetEmblem(checkingFiles ? EmblemState.Verify : UpdateAvailable ? EmblemState.Available : EmblemState.Idle);
            }
            bool available = UpdateAvailable;
            VersionText.Inlines.Clear();
            VersionText.Inlines.Add(new Run("Update " + mdUpdate));
            if (available)
            {
                VersionText.Inlines.Add(new Run("  ·  "));
                VersionText.Inlines.Add(new Run(latest.Title + " available") { Foreground = (Brush)FindResource("GoldBright"), FontWeight = FontWeights.SemiBold });
            }
            ChipText.Text = available ? latest.Title + " is out! Click to see what's new." : "";
            Chip.Visibility = available && !chipDismissed && page == Page.Play && UpdateSheet.Visibility != Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
            NavPlay.Uid = available && page != Page.Play && page != Page.Multiplayer ? "dot" : "";
            NavPlay.ToolTip = NavPlay.Uid == "dot" ? latest.Title + " is out!" : null;
            MultiplayerLock.Visibility = HasMultiplayer ? Visibility.Collapsed : Visibility.Visible;
            MultiplayerButton.ToolTip = HasMultiplayer ? null : "Multiplayer is unavailable.";
            SingleplayerButton.Visibility = checkingFiles ? Visibility.Hidden : Visibility.Visible;
            MultiplayerButton.Visibility = checkingFiles ? Visibility.Hidden : Visibility.Visible;
            StatusLeft.Visibility = checkingFiles ? Visibility.Visible : Visibility.Collapsed;
            StatusRight.Visibility = StatusLeft.Visibility;
        }

        private bool chipDismissed;

        private void SetEmblem(EmblemState state)
        {
            emblem = state;
            bool red = state == EmblemState.Available;
            bool teal = state == EmblemState.Verify || state == EmblemState.Ready;
            Fade(RedEmblem, red, TimeSpan.FromMilliseconds(350));
            Fade(TealEmblem, teal, TimeSpan.FromMilliseconds(350));

            CheckingArc.Visibility = state == EmblemState.Checking ? Visibility.Visible : Visibility.Collapsed;
            ArcTurn.BeginAnimation(RotateTransform.AngleProperty, state == EmblemState.Checking
                ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever } : null);

            // red: a slow pulse and two ripples; ready: three quick teal pulses
            PulseColor.Color = red ? Color.FromArgb(0xD9, 0xFF, 0x28, 0x32) : Color.FromArgb(0xD9, 0x32, 0xF0, 0xD2);
            if (red)
            {
                EmblemPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(0.9))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
                Ripple(Ripple1, 0);
                Ripple(Ripple2, 0.9);
            }
            else if (state == EmblemState.Ready)
            {
                EmblemPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(0.6))
                    { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3), EasingFunction = new SineEase() });
            }
            if (!red)
            {
                Ripple1.BeginAnimation(OpacityProperty, null);
                Ripple2.BeginAnimation(OpacityProperty, null);
                Ripple1.Opacity = Ripple2.Opacity = 0;
                if (state != EmblemState.Ready)
                {
                    EmblemPulse.BeginAnimation(OpacityProperty, null);
                    EmblemPulse.Opacity = 0;
                }
            }

            switch (state)
            {
                case EmblemState.Checking: Emblem.ToolTip = "Checking for updates..."; break;
                case EmblemState.Available: Emblem.ToolTip = null; break; // the reminder says it
                case EmblemState.Verify: Emblem.ToolTip = "Checking the new files..."; break;
                case EmblemState.Ready: Emblem.ToolTip = "Ready!"; break;
                default:
                    Emblem.ToolTip = settings.CheckUpdates ? "Update " + mdUpdate + ": you're up to date. Click to check again." : "Click to check for updates";
                    break;
            }
        }

        private static void Ripple(UIElement ring, double delay)
        {
            var scale = (ScaleTransform)ring.RenderTransform;
            var time = TimeSpan.FromSeconds(1.8);
            var begin = TimeSpan.FromSeconds(delay);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.55, time) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.55, time) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever });
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, 0, time) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever });
        }

        private void Emblem_MouseEnter(object sender, MouseEventArgs e)
        {
            PlaySfx(hoverSound);
        }

        // a press on the emblem (or the reminder) is a click, not the start of a window drag
        private void Emblem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void Emblem_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // not a window drag
            if (emblem == EmblemState.Available)
            {
                PlaySfx(clickSound);
                if (UpdateSheet.Visibility == Visibility.Visible)
                {
                    HideSheet();
                }
                else
                {
                    ShowSheet();
                }
            }
            else if (emblem == EmblemState.Idle)
            {
                PlaySfx(clickSound);
                LookForUpdate(true);
            }
        }

        private void ShowSheet()
        {
            if (latest == null)
            {
                return;
            }
            SheetTitle.Text = latest.Title + " is out";
            SheetLines.Children.Clear();
            foreach (string line in latest.WhatsNew.Take(6))
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                row.Children.Add(new TextBlock { Text = "•", Width = 18, FontSize = 13.5, Foreground = (Brush)FindResource("SoftText") });
                row.Children.Add(new TextBlock { Text = line, Style = (Style)FindResource("Body"), FontSize = 13.5, LineHeight = 19, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)) });
                SheetLines.Children.Add(row);
            }
            SheetNews.Visibility = ArticleById(latest.Article) != null ? Visibility.Visible : Visibility.Collapsed;
            UpdateSheet.Visibility = Visibility.Visible;
            var grow = new ScaleTransform(0.94, 0.94);
            UpdateSheet.RenderTransformOrigin = new Point(0.5, 1);
            UpdateSheet.RenderTransform = grow;
            var time = TimeSpan.FromMilliseconds(220);
            var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
            UpdateSheet.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            chipDismissed = true;
            ShowUpdate();
        }

        private void HideSheet()
        {
            UpdateSheet.Visibility = Visibility.Collapsed;
        }

        private void SheetModDB_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(latest?.ModDB);
            HideSheet();
        }

        private void SheetNexus_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(latest?.Nexus);
            HideSheet();
        }

        private void SheetNews_Click(object sender, RoutedEventArgs e)
        {
            Online.Article article = ArticleById(latest?.Article);
            if (article != null)
            {
                PlaySfx(clickSound);
                OpenArticle(article);
            }
        }

        private void SheetLater_Click(object sender, RoutedEventArgs e)
        {
            PlaySfx(backSound);
            HideSheet(); // the emblem stays red
        }

        // ---- the bars: they fill from the emblem outwards while the new files are checked ----

        private double fillShown, fillTarget;
        private DateTime fillLast;
        private int checkedFiles, totalFiles;
        private string checkedName = "";

        private void SetBladeColor(Color color, Color glow)
        {
            // the blade's colour with a dark edge and a bright middle line, like a lightsaber
            Brush Blade()
            {
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                brush.GradientStops.Add(new GradientStop(Shade(color, 0.62), 0));
                brush.GradientStops.Add(new GradientStop(color, 0.32));
                brush.GradientStops.Add(new GradientStop(Mix(color, Colors.White, 0.3), 0.5));
                brush.GradientStops.Add(new GradientStop(color, 0.68));
                brush.GradientStops.Add(new GradientStop(Shade(color, 0.62), 1));
                return brush;
            }
            BladeLeft.Fill = Blade();
            BladeRight.Fill = Blade();
            GlowLeft.Fill = GlowRight.Fill = new SolidColorBrush(glow);
        }

        private static Color Shade(Color c, double k)
        {
            return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        }

        private static Color Mix(Color a, Color b, double k)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
        }

        private void SetFill(double p)
        {
            p = Math.Max(0, Math.Min(1, p));
            double leftWidth = (BarLeftEnd - BarLeft) * p, rightWidth = (BarRightEnd - BarRight) * p;
            Canvas.SetLeft(BladeLeft, BarLeftEnd - leftWidth);
            BladeLeft.Width = leftWidth;
            BladeRight.Width = rightWidth;
            Canvas.SetLeft(GlowLeft, BarLeftEnd - leftWidth);
            GlowLeft.Width = leftWidth;
            Canvas.SetLeft(GlowRight, BarRight);
            GlowRight.Width = rightWidth;
        }

        // a newer update than last time is installed: check its files once, with the bars
        private async void CheckNewFiles()
        {
            checkingFiles = true;
            fillShown = fillTarget = 0;
            SetFill(0);
            SetBladeColor(Color.FromRgb(0x0C, 0x91, 0x80), Color.FromArgb(0xA6, 0x28, 0xE6, 0xC8));
            FillLayer.BeginAnimation(OpacityProperty, null);
            FillLayer.Opacity = 1;
            ShowUpdate();
            ShowFillStatus();
            fillLast = DateTime.Now;
            CompositionTarget.Rendering += FillStep;

            FilesCheck.Result result = await Task.Run(() => FilesCheck.Run(folder, (done, total, name) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    checkedFiles = done;
                    totalFiles = total;
                    checkedName = name;
                    fillTarget = total > 0 ? (double)done / total : 1;
                }))));
            lastCheck = result;
            fillTarget = 1;
        }

        // the shown fill follows the check, but takes at least 2.5 s, so the bars can be seen filling
        private void FillStep(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            double dt = (now - fillLast).TotalSeconds;
            fillLast = now;
            fillShown = Math.Min(fillTarget, fillShown + dt / 2.5);
            SetFill(fillShown);
            ShowFillStatus();
            if (fillShown >= 1 && lastCheck != null)
            {
                CompositionTarget.Rendering -= FillStep;
                FilesChecked();
            }
        }

        private void ShowFillStatus()
        {
            StatusLeft.Inlines.Clear();
            StatusLeft.Inlines.Add(new Run("CHECKING THE NEW FILES") { FontWeight = FontWeights.Bold });
            StatusLeft.Inlines.Add(new Run("   " + (int)(fillShown * 100) + " %"));
            StatusRight.Text = checkedName.Length > 0 ? ModFolder + "/" + checkedName : totalFiles > 0 ? checkedFiles + " of " + totalFiles + " files" : "";
        }

        private void FilesChecked()
        {
            bool unlocked = HasMultiplayer && settings.LastUpdate < 9;
            settings.LastUpdate = mdUpdate;
            settings.Save();
            int broken = lastCheck.Broken.Count;

            StatusLeft.Inlines.Clear();
            StatusLeft.Inlines.Add(new Run(broken == 0 ? "UPDATE " + mdUpdate + " IS READY" : broken + (broken == 1 ? " FILE" : " FILES") + " TO LOOK AT") { FontWeight = FontWeights.Bold });
            StatusRight.Text = broken == 0 ? "All files checked" : "See Settings";
            if (broken > 0)
            {
                SetBladeColor(Color.FromRgb(0x55, 0x55, 0x5A), Colors.Transparent);
            }
            else
            {
                SetEmblem(EmblemState.Ready);
                var shock = (ScaleTransform)Shock.RenderTransform;
                shock.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 5.5, TimeSpan.FromSeconds(0.95)));
                shock.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 5.5, TimeSpan.FromSeconds(0.95)));
                Shock.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.95)));
            }
            PlaySfx(clickSound);
            ShowInstallation();

            // the bars fade, the modes come back
            After(2.3, () =>
            {
                FillLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
                checkingFiles = false;
                if (emblem == EmblemState.Ready)
                {
                    After(3.6, () =>
                    {
                        if (emblem == EmblemState.Ready)
                        {
                            emblem = EmblemState.Idle;
                            ShowUpdate();
                        }
                    });
                }
                ShowUpdate();
                foreach (UIElement mode in new UIElement[] { SingleplayerButton, MultiplayerButton })
                {
                    mode.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(550)));
                }
                ShowToast(broken > 0 ? broken + " of the new files could not be read. Settings shows which."
                    : unlocked ? "Update " + mdUpdate + " is ready. Multiplayer is now available!" : "Update " + mdUpdate + " is ready. Have fun!");
            });
        }

        // ------------------------------------------------------------------------------------------
        // NEWS
        // ------------------------------------------------------------------------------------------

        private List<Online.Article> articles = new List<Online.Article>();
        private readonly Dictionary<string, ImageSource> pictures = new Dictionary<string, ImageSource>();

        private Online.Article ArticleById(string id)
        {
            return id == null ? null : articles.FirstOrDefault(a => a.Id == id);
        }

        private HashSet<string> SeenNews()
        {
            return new HashSet<string>(settings.SeenNews.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private bool newsLoading;

        private async void LoadNews()
        {
            if (newsLoading)
            {
                return;
            }
            newsLoading = true;
            NewsEmpty.Text = "Looking for news...";
            NewsRetry.Visibility = Visibility.Collapsed;
            string json = await Task.Run(() => Online.GetText("news.json"));
            newsLoading = false;
            articles = Online.ParseNews(json);
            NewsEmpty.Text = json == null ? "Could not load the news. Is the PC online?" : "No news yet.";
            NewsEmptyBox.Visibility = articles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NewsRetry.Visibility = articles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HashSet<string> seen = SeenNews();
            NewsCards.Children.Clear();
            foreach (Online.Article article in articles)
            {
                NewsCards.Children.Add(NewsCard(article, !seen.Contains(article.Id)));
            }
            ShowNewsButton();
            if (page == Page.News)
            {
                MarkNewsRead();
            }
            ShowUpdate(); // "What's new" in the update window needs the articles
            NewsScroll_ScrollChanged(null, null);

            // the pictures, one after the other
            foreach (Online.Article article in articles.ToList())
            {
                if (article.Image != null && !pictures.ContainsKey(article.Image))
                {
                    ImageSource picture = await Task.Run(() => Online.GetImage(article.Image));
                    pictures[article.Image] = picture;
                    foreach (Button card in NewsCards.Children.OfType<Button>().Where(c => c.Tag == article))
                    {
                        if (card.Content is Grid grid && grid.Children[0] is Image image)
                        {
                            image.Source = picture;
                        }
                    }
                }
            }
        }

        // no news (offline, or not published yet): look again
        private void NewsRetry_Click(object sender, RoutedEventArgs e)
        {
            PlaySfx(clickSound);
            LoadNews();
        }

        private Button NewsCard(Online.Article article, bool fresh)
        {
            var grid = new Grid { ClipToBounds = true };
            grid.Children.Add(new Image { Height = 318, VerticalAlignment = VerticalAlignment.Top, Stretch = Stretch.UniformToFill });
            var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x0B, 0x0B, 0x0D), 0));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0D), 0.88));
            grid.Children.Add(new Border { Height = 160, Margin = new Thickness(0, 168, 0, 0), VerticalAlignment = VerticalAlignment.Top, Background = fade });
            if (fresh)
            {
                grid.Children.Add(new Border
                {
                    Background = (Brush)FindResource("Red"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "NEW", FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White }
                });
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20, 0, 20, 15) };
            var meta = new TextBlock { FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
            meta.Inlines.Add(new Run(article.Tag.ToUpperInvariant()) { Foreground = (Brush)FindResource("GoldText") });
            meta.Inlines.Add(new Run("   " + article.Date.ToUpperInvariant()) { Foreground = (Brush)FindResource("DimText"), FontWeight = FontWeights.Normal });
            text.Children.Add(meta);
            text.Children.Add(new TextBlock { Text = article.Title, FontFamily = (FontFamily)FindResource("LabelFont"), FontSize = 22, Foreground = (Brush)FindResource("WhiteText"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = article.Summary, FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xD2)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), LineHeight = 19.5 });
            grid.Children.Add(text);

            var card = new Button { Style = (Style)FindResource("NewsCard"), Content = grid, Tag = article };
            card.Click += (s, e) =>
            {
                if (!newsDragged)
                {
                    PlaySfx(clickSound);
                    OpenArticle(article);
                }
            };
            return card;
        }

        private int unreadNews;
        private SolidColorBrush newsFlash;

        // the NEWS button flashes and shows a dot while there are articles that weren't opened yet
        private void ShowNewsButton()
        {
            HashSet<string> seen = SeenNews();
            unreadNews = articles.Count(a => !seen.Contains(a.Id));
            NavNews.Uid = unreadNews > 0 ? "dot" : "";
            NavNews.ToolTip = unreadNews > 0 ? (unreadNews == 1 ? "1 new article" : unreadNews + " new articles") : null;
            if (unreadNews > 0 && newsFlash == null)
            {
                newsFlash = new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xF9));
                NavNews.Foreground = newsFlash;
                newsFlash.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromRgb(0xFF, 0xCC, 0x66), TimeSpan.FromSeconds(0.55))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            else if (unreadNews == 0 && newsFlash != null)
            {
                newsFlash = null;
                NavNews.ClearValue(ForegroundProperty);
            }
        }

        private void MarkNewsRead()
        {
            if (articles.Count == 0)
            {
                return;
            }
            HashSet<string> seen = SeenNews();
            if (articles.All(a => seen.Contains(a.Id)))
            {
                return;
            }
            // keep the ids that are still published, so the list doesn't grow forever
            settings.SeenNews = string.Join(",", articles.Select(a => a.Id));
            settings.Save();
            ShowNewsButton();
        }

        private async void OpenArticle(Online.Article article)
        {
            pictures.TryGetValue(article.Image ?? "", out ImageSource picture);
            ArticleImage.Source = picture;
            var doc = new FlowDocument
            {
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDF)),
                PagePadding = new Thickness(48, 0, 48, 24),
                LineHeight = 22.5
            };
            doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 44, 0, 0) });
            var meta = new Paragraph(new Run((article.Tag + "  ·  " + article.Date).ToUpperInvariant()))
            {
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("GoldText"), Margin = new Thickness(0, 0, 0, 3)
            };
            doc.Blocks.Add(meta);
            doc.Blocks.Add(new Paragraph(new Run(article.Title))
            {
                FontFamily = (FontFamily)FindResource("LabelFont"), FontSize = 30, Foreground = (Brush)FindResource("WhiteText"), Margin = new Thickness(0, 0, 0, 12)
            });
            ArticleText.Document = doc;
            ArticleText.ScrollViewer()?.ScrollToTop();
            SetPage(Page.Article, true);

            string html = article.File == null ? null : await Task.Run(() => Online.GetText(article.File));
            if (ArticleText.Document != doc)
            {
                return; // another article was opened meanwhile
            }
            if (html == null)
            {
                doc.Blocks.Add(new Paragraph(new Run(article.Summary)));
                doc.Blocks.Add(new Paragraph(new Run("The full article could not be loaded. Is the PC online?")) { Foreground = (Brush)FindResource("DimText") });
                return;
            }
            var images = new Dictionary<string, ImageSource>();
            foreach (string src in HtmlText.Images(html).Distinct())
            {
                images[src] = await Task.Run(() => Online.GetImage(src));
            }
            HtmlText.AppendTo(doc, html, new HtmlText.Styles
            {
                Text = (FontFamily)FindResource("UiFont"),
                Heading = (FontFamily)FindResource("LabelFont"),
                Foreground = doc.Foreground,
                HeadingBrush = (Brush)FindResource("WhiteText"),
                LinkBrush = (Brush)FindResource("GoldBright"),
                NoteBackground = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xCC, 0x66)),
                NoteBorder = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xCC, 0x66))
            }, src => src != null && images.TryGetValue(src, out ImageSource image) ? image : null, OpenUrl);
        }

        private void ArticleBack_Click(object sender, RoutedEventArgs e)
        {
            SetPage(Page.News);
        }

        // ---- the gallery: drag it, throw it, use the wheel or the arrows; it settles on a card ----

        private bool newsDown, newsDragged;
        private double newsStartX, newsStartOffset, newsLastX, newsSpeed;
        private DateTime newsLastTime;
        private double wheelSum;
        private DateTime wheelLock;

        private void NewsScroll_MouseDown(object sender, MouseButtonEventArgs e)
        {
            StopScrollAnimation();
            newsDown = true;
            newsDragged = false;
            newsStartX = newsLastX = e.GetPosition(NewsScroll).X;
            newsStartOffset = NewsScroll.HorizontalOffset;
            newsLastTime = DateTime.Now;
            newsSpeed = 0;
        }

        private void NewsScroll_MouseMove(object sender, MouseEventArgs e)
        {
            if (!newsDown)
            {
                return;
            }
            double x = e.GetPosition(NewsScroll).X;
            if (!newsDragged && Math.Abs(x - newsStartX) > 5)
            {
                newsDragged = true;
                NewsScroll.CaptureMouse();
                NewsScroll.Cursor = Cursors.SizeWE;
            }
            if (!newsDragged)
            {
                return;
            }
            NewsScroll.ScrollToHorizontalOffset(newsStartOffset - (x - newsStartX));
            DateTime now = DateTime.Now;
            double dt = Math.Max(1, (now - newsLastTime).TotalMilliseconds);
            newsSpeed = 0.7 * newsSpeed + 0.3 * (newsLastX - x) / dt;
            newsLastX = x;
            newsLastTime = now;
        }

        private void NewsScroll_MouseUp(object sender, MouseButtonEventArgs e)
        {
            EndNewsDrag();
            if (newsDragged)
            {
                e.Handled = true; // a drag doesn't open the card
            }
        }

        private void NewsScroll_LostCapture(object sender, MouseEventArgs e)
        {
            EndNewsDrag();
        }

        private void EndNewsDrag()
        {
            if (!newsDown)
            {
                return;
            }
            newsDown = false;
            if (!newsDragged)
            {
                return;
            }
            NewsScroll.ClearValue(CursorProperty);
            if (NewsScroll.IsMouseCaptured)
            {
                NewsScroll.ReleaseMouseCapture();
            }
            if ((DateTime.Now - newsLastTime).TotalMilliseconds > 90)
            {
                newsSpeed = 0; // held still before letting go: no throw
            }
            ScrollNewsTo(NearestCard(NewsScroll.HorizontalOffset + newsSpeed * 220));
            // the click that ends a drag comes after this: forget the drag a moment later
            After(0.05, () => newsDragged = false);
        }

        private double NearestCard(double offset)
        {
            double max = NewsScroll.ScrollableWidth;
            double best = 0;
            for (int i = 0; i < NewsCards.Children.Count; i++)
            {
                double stop = Math.Min(max, i * CardStep);
                if (Math.Abs(stop - offset) < Math.Abs(best - offset))
                {
                    best = stop;
                }
            }
            return best;
        }

        // one wheel notch = one card; a touchpad's many small steps add up first
        private void NewsScroll_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            wheelSum += e.Delta;
            if (DateTime.Now < wheelLock || Math.Abs(wheelSum) < 40)
            {
                return;
            }
            int direction = wheelSum < 0 ? 1 : -1; // wheel down: the next card
            wheelSum = 0;
            wheelLock = DateTime.Now.AddMilliseconds(260);
            StepNews(direction);
        }

        private void StepNews(int direction)
        {
            double from = scrollAnimating ? scrollTo : NewsScroll.HorizontalOffset;
            double to = NearestCard(from + direction * CardStep);
            if (Math.Abs(to - from) < 1)
            {
                return;
            }
            PlaySfx(hoverSound);
            ScrollNewsTo(to);
        }

        private void NewsLeft_Click(object sender, RoutedEventArgs e)
        {
            StepNews(-1);
        }

        private void NewsRight_Click(object sender, RoutedEventArgs e)
        {
            StepNews(1);
        }

        private void NewsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            NewsLeft.Visibility = NewsScroll.HorizontalOffset > 8 ? Visibility.Visible : Visibility.Hidden;
            NewsRight.Visibility = NewsScroll.HorizontalOffset < NewsScroll.ScrollableWidth - 8 ? Visibility.Visible : Visibility.Hidden;
        }

        private bool scrollAnimating;
        private double scrollFrom, scrollTo;
        private DateTime scrollStart;

        private void ScrollNewsTo(double offset)
        {
            StopScrollAnimation();
            scrollFrom = NewsScroll.HorizontalOffset;
            scrollTo = offset;
            scrollStart = DateTime.Now;
            scrollAnimating = true;
            CompositionTarget.Rendering += ScrollStep;
        }

        private void ScrollStep(object sender, EventArgs e)
        {
            double q = Math.Min(1, (DateTime.Now - scrollStart).TotalMilliseconds / 340);
            double ease = 1 - Math.Pow(1 - q, 3);
            NewsScroll.ScrollToHorizontalOffset(scrollFrom + (scrollTo - scrollFrom) * ease);
            if (q >= 1)
            {
                StopScrollAnimation();
            }
        }

        private void StopScrollAnimation()
        {
            if (scrollAnimating)
            {
                CompositionTarget.Rendering -= ScrollStep;
                scrollAnimating = false;
            }
        }

        // ------------------------------------------------------------------------------------------
        // ABOUT
        // ------------------------------------------------------------------------------------------

        private List<TextContent.CreditSection> credits;
        private readonly DispatcherTimer creditsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly List<KeyValuePair<Button, FrameworkElement>> creditsSections = new List<KeyValuePair<Button, FrameworkElement>>();

        // a section of the credits list, for the heading that stays at the top while it is in view
        private sealed class CreditsTitle
        {
            public FrameworkElement Block;
            public string Title;
            public List<KeyValuePair<FrameworkElement, string>> Groups;
        }

        private readonly List<CreditsTitle> creditsTitles = new List<CreditsTitle>();

        private TextBlock Text(string text, double size, Brush brush, FontWeight weight, bool label = false)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = (FontFamily)FindResource(label ? "LabelFont" : "UiFont"),
                FontSize = size,
                Foreground = brush,
                FontWeight = weight,
                TextWrapping = TextWrapping.Wrap
            };
        }

        private void BuildAbout()
        {
            // the links
            var links = new[]
            {
                new[] { "ModDB", "Download", "https://www.moddb.com/mods/movie-duels", "ModDB" },
                new[] { "Nexus Mods", "Download", "https://www.nexusmods.com/starwarsjediknightjediacademy/mods/45", "Nexus" },
                new[] { "Discord", "Community and help", "https://discord.com/invite/PhQPy7j", "Discord" },
                new[] { "YouTube", "Trailers and guides", "https://www.youtube.com/@MovieDuels", "YouTube" },
                new[] { "JKHub", "Files and forum", "https://jkhub.org/files/file/3860-star-wars-movie-duels/", "JKHub" }
            };
            foreach (string[] link in links)
            {
                var row = new DockPanel();
                row.Children.Add(new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 11, 0), Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Icons/" + link[3] + ".png")) });
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(Text(link[0], 14, (Brush)FindResource("WhiteText"), FontWeights.SemiBold));
                words.Children.Add(Text(link[1], 12, (Brush)FindResource("DimText"), FontWeights.Normal));
                row.Children.Add(words);
                var button = new Button { Style = (Style)FindResource("RowButton"), Content = row, Height = 53, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 0, 3), ToolTip = link[2] };
                string url = link[2];
                button.Click += (s, e) => OpenUrl(url);
                button.MouseEnter += MenuButton_MouseEnter;
                LinkRows.Children.Add(button);
            }

            // About: the mod
            TextContent.AboutText about = TextContent.ParseAbout(ReadResourceText("Assets/Text/About.txt"));
            var top = new StackPanel();
            DockPanel.SetDock(top, Dock.Top);
            foreach (string paragraph in about.Paragraphs)
            {
                top.Children.Add(new TextBlock { Text = paragraph, Style = (Style)FindResource("Body"), Margin = new Thickness(0, 0, 12, 12) });
            }
            foreach (string bullet in about.Bullets)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 12, 3) };
                row.Children.Add(new TextBlock { Text = "•", Width = 19, Style = (Style)FindResource("Body") });
                row.Children.Add(new TextBlock { Text = bullet, Style = (Style)FindResource("Body") });
                top.Children.Add(row);
            }
            ModDock.Children.Add(top);
            if (about.SmallPrint != null)
            {
                var small = new TextBlock { Text = about.SmallPrint, Style = (Style)FindResource("Body"), FontSize = 11.5, LineHeight = 15.5, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x8D)), Margin = new Thickness(0, 12, 12, 0) };
                DockPanel.SetDock(small, Dock.Bottom);
                ModDock.Children.Add(small);
            }
            // the small print sits at the bottom of the card
            ModPanel.SizeChanged += (s, e) => ModDock.MinHeight = ModPanel.ViewportHeight;

            // FAQ: a question opens its answer; the open question stays gold
            foreach (TextContent.FaqGroup group in TextContent.ParseFaq(ReadResourceText("Assets/Text/FAQ.txt")))
            {
                if (group.Name.Length > 0)
                {
                    FaqList.Children.Add(new TextBlock { Text = group.Name.ToUpperInvariant(), Style = (Style)FindResource("Heading"), Margin = new Thickness(0, FaqList.Children.Count == 0 ? 0 : 18, 0, 2) });
                }
                foreach (var qa in group.Questions)
                {
                    FaqList.Children.Add(FaqItem(qa.Key, qa.Value));
                }
            }

            // Team
            foreach (TextContent.TeamGroup group in TextContent.ParseTeam(ReadResourceText("Assets/Text/Team.txt")))
            {
                TeamList.Children.Add(new TextBlock { Text = group.Name.ToUpperInvariant(), Style = (Style)FindResource("Heading"), Margin = new Thickness(0, TeamList.Children.Count == 0 ? 0 : 6, 0, 9) });
                if (group.People.Any(p => p.Value != null))
                {
                    var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 12) };
                    foreach (var person in group.People)
                    {
                        var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 9) };
                        cell.Children.Add(Text(person.Key, 13.5, (Brush)FindResource("WhiteText"), FontWeights.SemiBold));
                        if (person.Value != null)
                        {
                            cell.Children.Add(Text(person.Value, 11.5, (Brush)FindResource("DimText"), FontWeights.Normal));
                        }
                        grid.Children.Add(cell);
                    }
                    TeamList.Children.Add(grid);
                }
                else
                {
                    TeamList.Children.Add(new TextBlock { Text = string.Join(", ", group.People.Select(p => p.Key)), Style = (Style)FindResource("Body"), FontSize = 13.5, LineHeight = 20, Foreground = new SolidColorBrush(Color.FromRgb(0xBC, 0xBC, 0xC1)), Margin = new Thickness(0, 0, 0, 14) });
                }
            }

            creditsTimer.Tick += (s, e) =>
            {
                creditsTimer.Stop();
                ShowCredits(CreditsSearch.Text);
            };
        }

        private FrameworkElement FaqItem(string question, List<string> answer)
        {
            var box = new StackPanel();
            var head = new DockPanel();
            var mark = new TextBlock { Text = "+", FontSize = 18, Foreground = (Brush)FindResource("GoldText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0) };
            DockPanel.SetDock(mark, Dock.Right);
            head.Children.Add(mark);
            var title = Text(question, 14, (Brush)FindResource("WhiteText"), FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            var button = new Button { Style = (Style)FindResource("QuietButton"), Content = head, MinHeight = 39, Padding = new Thickness(0, 6, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            button.Template = PlainTemplate();

            var body = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
            var list = new List<string>();
            void Flush()
            {
                foreach (string item in list)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                    row.Children.Add(new TextBlock { Text = "•", Width = 18, Style = (Style)FindResource("Body"), FontSize = 13.5 });
                    row.Children.Add(new TextBlock { Text = item, Style = (Style)FindResource("Body"), FontSize = 13.5, LineHeight = 19.5, Foreground = (Brush)FindResource("SoftText") });
                    body.Children.Add(row);
                }
                list.Clear();
            }
            foreach (string line in answer)
            {
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    list.Add(line.Substring(2));
                    continue;
                }
                Flush();
                body.Children.Add(new TextBlock { Text = line, Style = (Style)FindResource("Body"), FontSize = 13.5, LineHeight = 19.5, Foreground = (Brush)FindResource("SoftText"), Margin = new Thickness(0, 0, 0, 6) });
            }
            Flush();

            button.MouseEnter += (s, e) =>
            {
                PlaySfx(hoverSound);
                title.Foreground = (Brush)FindResource("GoldText");
            };
            button.MouseLeave += (s, e) =>
            {
                if (body.Visibility != Visibility.Visible)
                {
                    title.Foreground = (Brush)FindResource("WhiteText");
                }
            };
            button.Click += (s, e) =>
            {
                PlaySfx(clickSound);
                bool open = body.Visibility != Visibility.Visible;
                body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                mark.Text = open ? "×" : "+";
                title.Foreground = (Brush)FindResource(open || button.IsMouseOver ? "GoldText" : "WhiteText");
            };
            box.Children.Add(button);
            box.Children.Add(body);
            return new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = box };
        }

        private static ControlTemplate PlainTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var back = new FrameworkElementFactory(typeof(Border));
            back.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            back.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, false);
            back.AppendChild(content);
            template.VisualTree = back;
            return template;
        }

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            PlaySfx(clickSound);
            ShowTab((Button)sender);
        }

        private void ShowTab(Button tab)
        {
            foreach (Button t in new[] { TabMod, TabFaq, TabTeam, TabCredits })
            {
                t.Tag = t == tab ? "on" : null;
            }
            ModPanel.Visibility = tab == TabMod ? Visibility.Visible : Visibility.Collapsed;
            FaqPanel.Visibility = tab == TabFaq ? Visibility.Visible : Visibility.Collapsed;
            TeamPanel.Visibility = tab == TabTeam ? Visibility.Visible : Visibility.Collapsed;
            bool all = tab == TabCredits;
            CreditsPanel.Visibility = all ? Visibility.Visible : Visibility.Collapsed;
            CreditsHead.Visibility = CreditsPanel.Visibility;
            // the credits take the whole card: the links step aside
            AboutLinks.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
            AboutDivider.Visibility = AboutLinks.Visibility;
            AboutMain.Margin = new Thickness(all ? 18 : 294, 14, 16, 14);
            if (all && credits == null)
            {
                credits = TextContent.ParseCredits(TextContent.ReadCreditsFile(Path.Combine(folder, ModFolder, "ReadMe", "ReadMe_Credits.txt")));
                ShowCredits("");
            }
        }

        private void CreditsSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            CreditsHint.Visibility = CreditsSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            creditsTimer.Stop();
            creditsTimer.Start();
        }

        // a text with the search match marked
        private TextBlock Marked(string text, string query, double size, Brush brush, FontWeight weight)
        {
            var block = Text("", size, brush, weight);
            int at = query.Length > 0 ? text.IndexOf(query, StringComparison.OrdinalIgnoreCase) : -1;
            if (at < 0)
            {
                block.Text = text;
                return block;
            }
            block.Inlines.Add(new Run(text.Substring(0, at)));
            block.Inlines.Add(new Run(text.Substring(at, query.Length)) { Background = new SolidColorBrush(Color.FromArgb(0x52, 0xFF, 0xCC, 0x66)) });
            block.Inlines.Add(new Run(text.Substring(at + query.Length)));
            return block;
        }

        private void ShowCredits(string query)
        {
            query = (query ?? "").Trim();
            CreditsList.Children.Clear();
            CreditsNav.Children.Clear();
            creditsSections.Clear();
            creditsTitles.Clear();
            CreditsPinned.Visibility = Visibility.Collapsed;
            if (credits.Count == 0)
            {
                CreditsList.Children.Add(new TextBlock { Text = "The credits are in MD\\ReadMe\\ReadMe_Credits.txt of the game folder, which wasn't found.", Style = (Style)FindResource("Body"), Foreground = (Brush)FindResource("DimText") });
                CreditsCount.Text = "";
                return;
            }
            bool Hit(TextContent.CreditEntry entry)
            {
                if (query.Length == 0)
                {
                    return true;
                }
                if (entry.Note != null)
                {
                    return false;
                }
                return entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || entry.Fields.Any(f => f.Value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            int total = 0;
            string lastGroup = null;
            TextBlock lastHeading = null;
            foreach (TextContent.CreditSection section in credits)
            {
                TextContent.SectionLabel(section.Name, out string groupName, out string label);
                var body = new StackPanel();
                var groupHeadings = new List<KeyValuePair<FrameworkElement, string>>();
                int count = 0;
                foreach (TextContent.CreditGroup group in section.Groups)
                {
                    List<TextContent.CreditEntry> entries = group.Entries.Where(Hit).ToList();
                    if (entries.Count == 0)
                    {
                        continue;
                    }
                    count += entries.Count(x => x.Note == null);
                    if (group.Name.Length > 0)
                    {
                        var groupHeading = new TextBlock { Text = group.Name.ToUpperInvariant(), Style = (Style)FindResource("Heading"), FontSize = 11, Margin = new Thickness(0, 12, 0, 7) };
                        body.Children.Add(groupHeading);
                        groupHeadings.Add(new KeyValuePair<FrameworkElement, string>(groupHeading, group.Name));
                    }
                    var grid = new UniformGrid { Columns = 3 };
                    foreach (TextContent.CreditEntry entry in entries)
                    {
                        if (entry.Note != null)
                        {
                            body.Children.Add(new Border
                            {
                                Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xCC, 0x66)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xCC, 0x66)),
                                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(0, 0, 0, 8),
                                Child = Text(entry.Note, 12.5, new SolidColorBrush(Color.FromRgb(0xE8, 0xD8, 0xB5)), FontWeights.Normal)
                            });
                            continue;
                        }
                        grid.Children.Add(CreditCard(entry, query));
                    }
                    if (grid.Children.Count > 0)
                    {
                        body.Children.Add(grid);
                    }
                }
                if (groupName != lastGroup)
                {
                    lastHeading = new TextBlock { Text = groupName.ToUpperInvariant(), Style = (Style)FindResource("Heading"), FontSize = 10.5, Margin = new Thickness(9, CreditsNav.Children.Count == 0 ? 2 : 10, 0, 3), Visibility = Visibility.Collapsed };
                    CreditsNav.Children.Add(lastHeading);
                    lastGroup = groupName;
                }
                if (count > 0)
                {
                    lastHeading.Visibility = Visibility.Visible;
                }
                var row = new DockPanel();
                var number = Text(count.ToString(), 11.5, new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x7C)), FontWeights.Normal);
                DockPanel.SetDock(number, Dock.Right);
                row.Children.Add(number);
                row.Children.Add(Text(label, 13, new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xCC)), FontWeights.Normal));
                var navButton = new Button { Style = (Style)FindResource("RowButton"), Content = row, MinHeight = 30, Padding = new Thickness(9, 5, 9, 5), Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed };
                CreditsNav.Children.Add(navButton);
                if (count == 0)
                {
                    continue;
                }
                total += count;
                string title = groupName == "Models / Skins" || groupName == "Weapons" ? groupName + ": " + label : label;
                var heading = new Border
                {
                    BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(0, 6, 0, 7),
                    Child = new TextBlock { Text = title, FontFamily = (FontFamily)FindResource("LabelFont"), FontSize = 19, Foreground = (Brush)FindResource("GoldText") }
                };
                var block = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                block.Children.Add(heading);
                block.Children.Add(body);
                CreditsList.Children.Add(block);
                creditsSections.Add(new KeyValuePair<Button, FrameworkElement>(navButton, block));
                creditsTitles.Add(new CreditsTitle { Block = block, Title = title, Groups = groupHeadings });
                navButton.Click += (s, e) =>
                {
                    PlaySfx(clickSound);
                    CreditsScroll.ScrollToVerticalOffset(block.TranslatePoint(new Point(0, 0), CreditsList).Y);
                    MarkCreditsSection(navButton);
                };
            }
            if (total == 0)
            {
                CreditsList.Children.Add(new TextBlock { Text = "Nothing found for “" + query + "”.", Style = (Style)FindResource("Body"), Foreground = (Brush)FindResource("DimText"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) });
            }
            CreditsCount.Text = query.Length > 0 ? total + " found" : total + " entries";
            CreditsScroll.ScrollToTop();
            MarkCreditsSection(creditsSections.Count > 0 ? creditsSections[0].Key : null);
        }

        private FrameworkElement CreditCard(TextContent.CreditEntry entry, string query)
        {
            var card = new StackPanel();
            card.Children.Add(Marked(entry.Name, query, 13.5, (Brush)FindResource("WhiteText"), FontWeights.SemiBold));
            ((TextBlock)card.Children[0]).Margin = new Thickness(0, 0, 0, 3);
            foreach (var field in entry.Fields)
            {
                TextBlock line = Marked(field.Value, query, 12, new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD4)), FontWeights.Normal);
                if (field.Key.Length > 0)
                {
                    line.Inlines.InsertBefore(line.Inlines.FirstInline ?? new Run(""), new Run(field.Key + ": ") { Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x94)) });
                    if (line.Inlines.Count == 1)
                    {
                        line.Inlines.Add(new Run(field.Value));
                    }
                }
                card.Children.Add(line);
            }
            if (entry.Links.Count > 0)
            {
                var links = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
                foreach (string url in entry.Links.Where(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    var link = new Button { Style = (Style)FindResource("LinkChip"), Content = TextContent.LinkName(url) + " ↗", ToolTip = url };
                    link.Click += (s, e) => OpenUrl(url);
                    links.Children.Add(link);
                }
                card.Children.Add(links);
            }
            return new Border { Style = (Style)FindResource("CreditCard"), Child = card };
        }

        private void MarkCreditsSection(Button current)
        {
            foreach (var section in creditsSections)
            {
                section.Key.Tag = section.Key == current ? "on" : null;
            }
        }

        // the section in view is marked in the list on the left, and its heading (with the group in view) stays at the top
        private void CreditsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0 || creditsSections.Count == 0)
            {
                return;
            }
            double offset = CreditsScroll.VerticalOffset;
            int current = 0;
            for (int i = 0; i < creditsSections.Count; i++)
            {
                if (creditsSections[i].Value.TranslatePoint(new Point(0, 0), CreditsList).Y <= offset + 6)
                {
                    current = i;
                }
            }
            MarkCreditsSection(creditsSections[current].Key);

            CreditsTitle section = creditsTitles[current];
            double top = section.Block.TranslatePoint(new Point(0, 0), CreditsList).Y;
            if (offset <= top + 2)
            {
                CreditsPinned.Visibility = Visibility.Collapsed; // its own heading is still in view
                return;
            }
            string group = null;
            foreach (var g in section.Groups)
            {
                if (g.Key.TranslatePoint(new Point(0, 0), CreditsList).Y <= offset + 40)
                {
                    group = g.Value;
                }
            }
            CreditsPinnedText.Inlines.Clear();
            CreditsPinnedText.Inlines.Add(new Run(section.Title));
            if (group != null)
            {
                CreditsPinnedText.Inlines.Add(new Run("   ·   " + group.ToUpperInvariant())
                {
                    FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("DimText"), BaselineAlignment = BaselineAlignment.Center
                });
            }
            CreditsPinned.Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------------------------------
        // SETTINGS
        // ------------------------------------------------------------------------------------------

        private void ShowOptions()
        {
            RendererVanilla.Tag = settings.Rend2 ? null : "on";
            RendererRend2.Tag = settings.Rend2 ? "on" : null;
            DisplayFullscreen.Tag = settings.Windowed ? null : "on";
            DisplayWindowed.Tag = settings.Windowed ? "on" : null;
            ResolutionText.Text = settings.ResolutionText;
            ControllerOff.Tag = settings.Controller ? null : "on";
            ControllerOn.Tag = settings.Controller ? "on" : null;
            MusicOff.Tag = settings.Music ? null : "on";
            MusicOn.Tag = settings.Music ? "on" : null;
            UpdatesOff.Tag = settings.CheckUpdates ? null : "on";
            UpdatesOn.Tag = settings.CheckUpdates ? "on" : null;
        }

        private void ShowMusicButton()
        {
            // speaker: waves when the music is on, a cross when it is off
            MusicOnMark.Visibility = settings.Music ? Visibility.Visible : Visibility.Collapsed;
            MusicOffMark.Visibility = settings.Music ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Renderer_Click(object sender, RoutedEventArgs e)
        {
            bool rend2 = sender == RendererRend2;
            if (settings.Rend2 != rend2)
            {
                settings.Rend2 = rend2;
                OptionChanged(true);
            }
        }

        private void Display_Click(object sender, RoutedEventArgs e)
        {
            bool windowed = sender == DisplayWindowed;
            if (settings.Windowed != windowed)
            {
                settings.Windowed = windowed;
                OptionChanged(true);
            }
        }

        private void Resolution_Click(object sender, RoutedEventArgs e)
        {
            settings.NextResolution();
            OptionChanged(true);
        }

        private void ResolutionPrev_Click(object sender, RoutedEventArgs e)
        {
            settings.PreviousResolution();
            OptionChanged(true);
        }

        private void Controller_Click(object sender, RoutedEventArgs e)
        {
            bool on = sender == ControllerOn;
            if (settings.Controller != on)
            {
                settings.Controller = on;
                OptionChanged(true);
            }
        }

        private void MusicSetting_Click(object sender, RoutedEventArgs e)
        {
            if (settings.Music != (sender == MusicOn))
            {
                Music_Click(sender, e);
            }
        }

        private void Updates_Click(object sender, RoutedEventArgs e)
        {
            bool on = sender == UpdatesOn;
            if (settings.CheckUpdates != on)
            {
                settings.CheckUpdates = on;
                OptionChanged();
                if (on && latest == null)
                {
                    LookForUpdate(false);
                }
                ShowUpdate();
            }
        }

        private void OptionChanged(bool gameConfig = false)
        {
            PlaySfx(clickSound);
            settings.Save();
            if (gameConfig)
            {
                settings.SaveGameConfig(); // renderer, display, resolution, controller: into the game's configs
            }
            ShowOptions();
        }

        // the rows under INSTALLATION: what was found, and what the last check said
        private void ShowInstallation()
        {
            InstallRows.Children.Clear();
            bool jka = FilesCheck.HasJediAcademy(folder);
            int pk3 = FilesCheck.Pk3Files(folder).Length;
            AddInstallRow(jka ? "ok" : "bad", "Jedi Academy", jka ? "Found: base\\assets0-3.pk3" : "Not found next to the MD folder");
            string mod;
            string state;
            if (pk3 == 0)
            {
                state = "bad";
                mod = "No pk3 files in the MD folder";
            }
            else if (lastCheck != null && lastCheck.Broken.Count > 0)
            {
                state = "warn";
                mod = "Update " + mdUpdate + " · can't read " + string.Join(", ", lastCheck.Broken.Take(3)) + (lastCheck.Broken.Count > 3 ? " ..." : "");
            }
            else
            {
                state = "ok";
                mod = "Update " + mdUpdate + " · " + pk3 + " pk3 files" + (lastCheck != null ? ", all can be read" : "");
            }
            AddInstallRow(state, "Movie Duels", mod);

            // the optional add-ons: installed, partly there, or where to get them
            AddonRows.Children.Clear();
            foreach (FilesCheck.Addon addon in FilesCheck.Addons)
            {
                List<string> missing = FilesCheck.Missing(folder, addon);
                if (missing.Count == 0)
                {
                    AddInstallRow("ok", addon.Name, "Installed", AddonRows, 150);
                }
                else if (missing.Count == addon.Files.Length)
                {
                    AddInstallRow("skip", addon.Name, "Not installed", AddonRows, 150, addon.Page);
                }
                else
                {
                    AddInstallRow("warn", addon.Name, "Missing " + string.Join(", ", missing), AddonRows, 150, addon.Page);
                }
            }
        }

        // a row with a mark, a name and what was found; link: a "Get it" button that opens the page
        private void AddInstallRow(string state, string name, string text, Panel rows = null, double nameWidth = 94, string link = null)
        {
            var row = new DockPanel { MinHeight = 30 };
            Color color = state == "ok" ? Color.FromRgb(0x2F, 0xD6, 0xBF) : state == "warn" ? Color.FromRgb(0xFF, 0xCC, 0x66)
                : state == "skip" ? Color.FromRgb(0xA0, 0xA0, 0xA5) : Color.FromRgb(0xFF, 0x8F, 0x7A);
            var mark = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
                Child = new TextBlock { Text = state == "ok" ? "✓" : state == "warn" ? "!" : state == "skip" ? "–" : "✕", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            row.Children.Add(mark);
            var title = Text(name, 12.5, (Brush)FindResource("WhiteText"), FontWeights.SemiBold);
            title.Width = nameWidth;
            title.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(title);
            var detail = Text(text, 12, new SolidColorBrush(Color.FromRgb(0xB9, 0xB9, 0xBE)), FontWeights.Normal);
            detail.TextWrapping = TextWrapping.NoWrap;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            detail.VerticalAlignment = VerticalAlignment.Center;
            detail.ToolTip = text;
            if (link != null)
            {
                var get = new Button { Style = (Style)FindResource("LinkChip"), Content = "Get it ↗", ToolTip = link, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                get.Click += (s, e) => OpenUrl(link);
                DockPanel.SetDock(get, Dock.Right);
                row.Children.Add(get);
            }
            row.Children.Add(detail);
            (rows ?? InstallRows).Children.Add(row);
        }

        private async void CheckInstallation_Click(object sender, RoutedEventArgs e)
        {
            if (checkingFiles)
            {
                ShowToast("The new files are being checked already.");
                return;
            }
            PlaySfx(clickSound);
            CheckButton.IsEnabled = false;
            CheckProgress.Visibility = Visibility.Visible;
            CheckBar.Width = 0;
            CheckText.Text = "Checking the files...";
            CheckProgress.UpdateLayout();
            double width = ((FrameworkElement)CheckBar.Parent).ActualWidth;
            var started = DateTime.Now;
            FilesCheck.Result result = await Task.Run(() => FilesCheck.Run(folder, (done, total, name) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    CheckBar.Width = total > 0 ? width * done / total : width;
                    CheckText.Text = "CHECKING FILES  " + (total > 0 ? done * 100 / total : 100) + " %   ·   " + (name.Length > 0 ? name : total + " of " + total);
                }))));
            double left = 1.0 - (DateTime.Now - started).TotalSeconds;
            if (left > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(left));
            }
            lastCheck = result;
            CheckBar.Width = width;
            int broken = result.Broken.Count;
            CheckText.Text = !result.JediAcademy ? "Jedi Academy was not found next to the MD folder."
                : broken == 0 ? "All " + result.Pk3Count + " files can be read."
                : broken + (broken == 1 ? " file" : " files") + " can't be read. Get them again from ModDB.";
            PlaySfx(clickSound);
            ShowInstallation();
            CheckButton.IsEnabled = true;
        }

        // Documents\My Games\MovieDuels\MD: the game writes it the first time it runs
        private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            string config = settings.ConfigFolder;
            if (config == null || !Directory.Exists(config))
            {
                PlaySfx(clickSound);
                ShowToast("The config folder is made when the game runs for the first time.");
                return;
            }
            PlaySfx(clickSound);
            try
            {
                Process.Start(new ProcessStartInfo(config) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            PlaySfx(clickSound);
            try
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------------------------------
        // Starting the game, links
        // ------------------------------------------------------------------------------------------

        private bool StartGame(bool multiplayer)
        {
            string exe = Path.Combine(folder, multiplayer ? MPExe : SPExe);
            if (!File.Exists(exe))
            {
                MessageBox.Show(this, "Could not find " + exe + "\n\nThe launcher must be in the MovieDuels game folder.",
                    "MovieDuels", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // "+set" on the command line overrides the values in the configs (they are the configs' own values unless the
            // SETTINGS page changed them, and then it wrote them into the configs too)
            string args = "";
            foreach (var kv in settings.GameConfigValues(multiplayer))
            {
                args += (args.Length > 0 ? " " : "") + "+set " + kv.Key + " " + kv.Value;
            }

            try
            {
                var startInfo = new ProcessStartInfo(exe, args) { WorkingDirectory = folder, UseShellExecute = false };
                startInfo.EnvironmentVariables[LauncherEnv] = LauncherToken; // lets a release build of the game start
                Process.Start(startInfo)?.Dispose();
                return true;
            }
            catch (Exception)
            {
                MessageBox.Show(this, "Could not start " + exe, "MovieDuels", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void Play(bool multiplayer)
        {
            PlaySfx(clickSound, true); // the launcher closes right after
            if (StartGame(multiplayer))
            {
                Close();
            }
        }

        private void Singleplayer_Click(object sender, RoutedEventArgs e)
        {
            Play(false);
        }

        private void Multiplayer_Click(object sender, RoutedEventArgs e)
        {
            PlaySfx(clickSound);
            if (!HasMultiplayer)
            {
                ShowToast("Multiplayer is coming soon.");
                return;
            }
            SetPage(Page.Multiplayer, true);
        }

        private void StartMultiplayer_Click(object sender, RoutedEventArgs e)
        {
            Play(true);
        }

        private void StartServer_Click(object sender, RoutedEventArgs e)
        {
            PlaySfx(clickSound);
            if (StartServer()) // the launcher stays open, to start the game and join
            {
                ShowToast("The server opens in its own window. You can join it now.");
            }
        }

        private void MultiplayerBack_Click(object sender, RoutedEventArgs e)
        {
            SetPage(Page.Play);
        }

        // Runs the FFA dedicated server batch file from the MD folder in a console window of its own. The batch file
        // restarts the server when it crashes; closing its window stops it.
        private bool StartServer()
        {
            string modFolder = Path.Combine(folder, ModFolder);
            string bat = Path.Combine(modFolder, ServerBat);
            if (!File.Exists(bat))
            {
                MessageBox.Show(this, "Could not find " + bat + "\n\nThe launcher must be in the MovieDuels game folder.",
                    "MovieDuels", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            string comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(comspec))
            {
                comspec = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            }

            try
            {
                // cmd /c ""<bat>"": the outer quotes are cmd's, the inner ones keep the spaces in the path. Started from
                // a window program without a console, cmd gets a console window of its own.
                var startInfo = new ProcessStartInfo(comspec, "/c \"\"" + bat + "\"\"")
                {
                    WorkingDirectory = modFolder,
                    UseShellExecute = false
                };
                startInfo.EnvironmentVariables[LauncherEnv] = LauncherToken; // the server inherits it, as the game does
                Process.Start(startInfo)?.Dispose();
                return true;
            }
            catch (Exception)
            {
                MessageBox.Show(this, "Could not start " + bat, "MovieDuels", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // A link button: the web address is the button's Tag.
        private void Link_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl((sender as FrameworkElement)?.Tag as string);
        }

        // only web pages: a link from the news can't start anything else
        private void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return; // link not set
            }
            PlaySfx(clickSound);
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception)
            {
                // no browser
            }
        }

        // ------------------------------------------------------------------------------------------
        // Window
        // ------------------------------------------------------------------------------------------

        // Moved onto a menu button (all buttons but the window buttons).
        private void MenuButton_MouseEnter(object sender, MouseEventArgs e)
        {
            PlaySfx(hoverSound);
        }

        // Hovering a mode lights up its side of the art: its glow layer fades in.
        private void ModeButton_MouseChanged(object sender, MouseEventArgs e)
        {
            UpdateGlow();
        }

        private bool multiplayerSide; // the side the mouse was on last stays lit; singleplayer at first

        private void UpdateGlow()
        {
            if (SingleplayerButton.IsMouseOver)
            {
                multiplayerSide = false;
            }
            else if (MultiplayerButton.IsMouseOver)
            {
                multiplayerSide = true;
            }
            bool play = page == Page.Play && !checkingFiles;
            Fade(SingleplayerGlow, play && !multiplayerSide);
            Fade(MultiplayerGlow, play && multiplayerSide);
        }

        private static void Fade(UIElement layer, bool show)
        {
            Fade(layer, show, FadeTime);
        }

        private static void Fade(UIElement layer, bool show, TimeSpan time)
        {
            layer.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1.0 : 0.0, time));
        }

        private void Music_Click(object sender, RoutedEventArgs e)
        {
            PlaySfx(clickSound);
            settings.Music = !settings.Music;
            settings.Save();
            ShowMusicButton();
            ShowOptions();
            if (settings.Music)
            {
                StartMusic();
            }
            else
            {
                StopMusic();
            }
        }

        private void Minimise_Click(object sender, RoutedEventArgs e)
        {
            PlaySfx(clickSound);
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            PlaySfx(backSound, true);
            Close();
        }

        // drag the window by anything that is not a button
        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
            {
                return;
            }
            e.Handled = true;
            if (CreditsSearch.IsKeyboardFocused && CreditsSearch.Text.Length > 0)
            {
                CreditsSearch.Text = ""; // first Esc clears the search
            }
            else if (UpdateSheet.Visibility == Visibility.Visible)
            {
                PlaySfx(backSound);
                HideSheet();
            }
            else if (page == Page.Article)
            {
                SetPage(Page.News);
            }
            else if (page != Page.Play)
            {
                SetPage(Page.Play);
            }
            else
            {
                PlaySfx(backSound, true);
                Close();
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            StopMusic();
            try
            {
                if (musicFile != null)
                {
                    File.Delete(musicFile);
                }
            }
            catch (Exception)
            {
                // still in use (a second launcher): it stays in %TEMP%
            }
        }
    }

    internal static class FlowViewerExtensions
    {
        // the scroll viewer inside a FlowDocumentScrollViewer (to start a new article at the top)
        public static ScrollViewer ScrollViewer(this FlowDocumentScrollViewer viewer)
        {
            return viewer.Template?.FindName("PART_ContentHost", viewer) as ScrollViewer;
        }
    }
}
