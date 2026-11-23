using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

internal sealed class Roth : Window
{
    private const double TitleBarHeight = 60;
    private static readonly string Root = Path.GetDirectoryName(typeof(Roth).Assembly.Location);
    private static readonly Brush Ink = Color("#F6F2F3"), Muted = Color("#ADA3AB"), Red = Color("#F05A68");
    private static readonly Regex EncodeProgress = new Regex(
        @"Encoding:\s+task\s+(\d+)\s+of\s+(\d+),\s*([\d.]+)\s*%",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly Preset[] profiles = {
        new Preset("Ultra Small", "480p", "For when file size matters most.", 480, 0, 26, 48, "slow"),
        new Preset("Medium", "540p", "Good for most clips.", 540, 0, 23, 64, "slow"),
        new Preset("High Quality", "720p", "Keeps more fine detail.", 720, 0, 22, 64, "slow")
    };
    private readonly ControlTemplate buttons;
    private readonly Grid shell = new Grid();
    private readonly MediaPlayer completionSound = new MediaPlayer();
    private readonly DispatcherTimer playbackClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Button[] cards = new Button[3];
    private Button advanced;
    private Preset custom;
    private Button pick, compress, save, play, cancel;
    private CancellationTokenSource jobCancellation;
    private TextBlock filename, detail, status, empty, resultInfo;
    private MediaElement video;
    private Slider timeline;
    private TextBlock timecode;
    private DockPanel playbackBar;
    private bool updatingTimeline, previewReady;
    private StackPanel presetList;
    private ProgressBar progress;
    private int selected;
    private string input, result, previewCopy, jobDir;
    private bool busy, playing;
    private bool batchRunning;
    private StackPanel batchRows;
    private ScrollViewer batchPanel;
    private readonly List<BatchJob> batch = new List<BatchJob>();

    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application();
        var window = new Roth();
        // Optional local file argument for repeatable smoke tests and direct launch.
        if (args.Length == 1 && File.Exists(args[0])) window.LoadFile(Path.GetFullPath(args[0]));
        app.Run(window);
    }

    private Roth()
    {
        Title = "ROTH Video Compressor";
        FontFamily = new FontFamily("Segoe UI");
        Background = Color("#100E13");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome {
            CaptionHeight = TitleBarHeight,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
        WindowState = WindowState.Maximized;
        Width = 1120;
        Height = 740;
        MinWidth = 820;
        MinHeight = 600;
        buttons = (ControlTemplate)XamlReader.Parse(
            @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
                <Border x:Name='surface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                        BorderThickness='1' CornerRadius='12' Padding='{TemplateBinding Padding}'>
                    <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='surface' Property='BorderBrush' Value='#EE6573'/></Trigger>
                    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='surface' Property='BorderBrush' Value='#FF929D'/></Trigger>
                    <Trigger Property='IsPressed' Value='True'><Setter TargetName='surface' Property='Opacity' Value='.74'/></Trigger>
                    <Trigger Property='IsEnabled' Value='False'><Setter TargetName='surface' Property='Opacity' Value='.43'/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>");
        Content = shell;
        BuildWorkspace();
        Closing += (s, e) => { if (busy || batchRunning) e.Cancel = true; };
        Closed += (s, e) => {
            playbackClock.Stop();
            completionSound.Close();
            video.Close();
            try { if (jobDir != null) Directory.Delete(jobDir, true); } catch (IOException) {} catch (UnauthorizedAccessException) {}
        };
        KeyDown += (s, e) => {
            if (e.Key == Key.F11)
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        };
    }

    private void BuildWorkspace()
    {
        var frame = new DockPanel();
        shell.Children.Add(frame);
        var header = new DockPanel { Height = TitleBarHeight, LastChildFill = true, Margin = new Thickness(32, 0, 24, 0) };
        DockPanel.SetDock(header, Dock.Top);
        frame.Children.Add(header);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        WindowChrome.SetIsHitTestVisibleInChrome(controls, true);
        DockPanel.SetDock(controls, Dock.Right);
        header.Children.Add(controls);
        foreach (var pair in new[] { "−", "□", "×" })
        {
            var control = Button(pair, false);
            control.Width = 38;
            control.Height = 34;
            control.Padding = new Thickness(0);
            control.Margin = new Thickness(6, 0, 0, 0);
            control.Click += (s, e) => {
                if (pair == "×") Close();
                else if (pair == "−") WindowState = WindowState.Minimized;
                else WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            };
            controls.Children.Add(control);
        }
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(Mark(30));
        var wordmark = Label("ROTH Video Compressor", 16);
        wordmark.Margin = new Thickness(10, 0, 22, 0);
        brand.Children.Add(wordmark);
        header.Children.Add(brand);

        var layout = new Grid { Margin = new Thickness(32, 12, 32, 28) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        frame.Children.Add(layout);

        var left = new StackPanel();
        var leftPanel = new DockPanel();
        var actions = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        leftPanel.Children.Add(actions);
        leftPanel.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        layout.Children.Add(leftPanel);
        left.Children.Add(Label("Compress a video", 27));
        var sub = Label("Choose a file, then pick a preset.", 13, Muted);
        sub.Margin = new Thickness(0, 4, 0, 22);
        left.Children.Add(sub);
        pick = Button("Choose Video", false);
        pick.Click += (s, e) => PickFile();
        pick.AllowDrop = true;
        pick.Drop += (s, e) => {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (!busy && !batchRunning && paths != null) ChooseFiles(paths);
        };
        left.Children.Add(pick);
        filename = Label("Drop a video here or browse", 12, Muted);
        filename.Margin = new Thickness(2, 8, 0, 20);
        filename.TextTrimming = TextTrimming.CharacterEllipsis;
        left.Children.Add(filename);
        left.Children.Add(Label("Preset", 16));
        presetList = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        left.Children.Add(presetList);
        for (int i = 0; i < profiles.Length; i++)
        {
            int index = i;
            Preset p = profiles[i];
            var card = Button("", false);
            card.Height = 43;
            card.Margin = new Thickness(0, 0, 0, 5);
            card.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var line = new DockPanel();
            var size = Label(p.Size, 11, Muted);
            DockPanel.SetDock(size, Dock.Right);
            line.Children.Add(size);
            line.Children.Add(Label(p.Name, 13));
            card.Content = line;
            card.ToolTip = new ToolTip {
                Background = Color("#251B24"), Foreground = Ink, BorderBrush = Color("#69404A"),
                Padding = new Thickness(12), Content = Label(p.Detail, 13)
            };
            ToolTipService.SetInitialShowDelay(card, 180);
            card.MouseEnter += (s, e) => detail.Text = p.Detail;
            card.MouseLeave += (s, e) => detail.Text = ActivePreset.Detail;
            card.Click += (s, e) => SelectPreset(index);
            presetList.Children.Add(card);
            cards[i] = card;
        }
        detail = Label("", 12, Muted);
        detail.TextWrapping = TextWrapping.Wrap;
        detail.MinHeight = 53;
        detail.Margin = new Thickness(2, 5, 0, 8);
        left.Children.Add(detail);
        advanced = Button("Advanced settings…", false);
        advanced.Height = 34;
        advanced.HorizontalAlignment = HorizontalAlignment.Left;
        advanced.Click += (s, e) => EditAdvanced();
        left.Children.Add(advanced);
        SelectPreset(0);
        compress = Button("Compress Video", true);
        compress.IsEnabled = false;
        compress.Click += async (s, e) => {
            if (!File.Exists(EncoderPath))
            {
                status.Text = "HandBrakeCLI is missing. Extract the full ROTH ZIP into one folder.";
                return;
            }
            if (batch.Count > 1) await EncodeBatch();
            else await Encode();
        };
        actions.Children.Add(compress);
        cancel = Button("Cancel", false);
        cancel.Visibility = Visibility.Collapsed;
        cancel.Click += (s, e) => {
            if (jobCancellation == null) return;
            cancel.IsEnabled = false;
            status.Text = "Cancelling…";
            jobCancellation.Cancel();
        };
        actions.Children.Add(cancel);

        var panel = new DockPanel { Margin = new Thickness(18) };
        var preview = new Border { Child = panel, Background = Color("#18151B"), BorderBrush = Color("#332A33"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(24) };
        Grid.SetColumn(preview, 2);
        layout.Children.Add(preview);
        var title = Label("Preview", 17);
        title.Margin = new Thickness(6, 0, 0, 14);
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        batchRows = new StackPanel();
        batchPanel = new ScrollViewer { Content = batchRows, MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        DockPanel.SetDock(batchPanel, Dock.Top);
        panel.Children.Add(batchPanel);
        var footer = new DockPanel { Margin = new Thickness(4, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        save = Button("Save As…", true);
        save.IsEnabled = false;
        save.Click += (s, e) => Save();
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(save);
        play = Button("▶  Play", false);
        play.Visibility = Visibility.Collapsed;
        play.Margin = new Thickness(0, 0, 10, 0);
        play.Click += (s, e) => SetPlayback(!playing);
        DockPanel.SetDock(play, Dock.Right);
        footer.Children.Add(play);
        var messages = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        resultInfo = Label("", 14);
        status = Label("Choose a video to get started", 12, Muted);
        status.TextWrapping = TextWrapping.Wrap;
        messages.Children.Add(resultInfo);
        messages.Children.Add(status);
        progress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed,
            Foreground = Red, Background = Color("#332A33"), Margin = new Thickness(0, 5, 10, 0) };
        messages.Children.Add(progress);
        footer.Children.Add(messages);

        playbackBar = new DockPanel { Margin = new Thickness(4, 12, 0, 0), Visibility = Visibility.Collapsed };
        DockPanel.SetDock(playbackBar, Dock.Bottom);
        panel.Children.Add(playbackBar);
        timecode = Label("0:00 / 0:00", 12, Muted);
        timecode.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(timecode, Dock.Right);
        playbackBar.Children.Add(timecode);
        timeline = new Slider { Minimum = 0, Maximum = 1, Height = 21, IsMoveToPointEnabled = true,
            SmallChange = 5, LargeChange = 15, Background = Color("#44323D"), Foreground = Red };
        System.Windows.Automation.AutomationProperties.SetName(timeline, "Seek video");
        timeline.Template = (ControlTemplate)XamlReader.Parse(
            @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>
                <Grid Background='Transparent'>
                    <Border Height='4' CornerRadius='2' Background='{TemplateBinding Background}'/>
                    <Track x:Name='PART_Track'>
                        <Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge'>
                            <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>
                                <Border Height='4' CornerRadius='2' Background='#C43B53'/>
                            </ControlTemplate></RepeatButton.Template>
                        </RepeatButton></Track.DecreaseRepeatButton>
                        <Track.Thumb><Thumb Width='13' Height='13' Cursor='Hand'>
                            <Thumb.Template><ControlTemplate TargetType='Thumb'><Ellipse Fill='#F05A68'/></ControlTemplate></Thumb.Template>
                        </Thumb></Track.Thumb>
                        <Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge'>
                            <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>
                                <Border Background='Transparent'/>
                            </ControlTemplate></RepeatButton.Template>
                        </RepeatButton></Track.IncreaseRepeatButton>
                    </Track>
                </Grid>
            </ControlTemplate>");
        timeline.ValueChanged += (s, e) => {
            if (previewReady && !updatingTimeline)
            {
                video.Position = TimeSpan.FromSeconds(e.NewValue);
                UpdateTimecode();
            }
        };
        playbackBar.Children.Add(timeline);
        playbackClock.Tick += (s, e) => SyncTimeline();

        var screen = new Grid { Background = Color("#0F0D11"), ClipToBounds = true };
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(15), Background = screen.Background, Child = screen });
        video = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform, Visibility = Visibility.Hidden, ScrubbingEnabled = true };
        video.MediaOpened += (s, e) => {
            if (result == null) return;
            empty.Visibility = Visibility.Collapsed;
            video.Visibility = Visibility.Visible;
            play.Visibility = Visibility.Visible;
            SetPlayback(false);
            video.Position = TimeSpan.Zero;
            if (video.NaturalDuration.HasTimeSpan && video.NaturalDuration.TimeSpan.TotalSeconds > 0)
            {
                timeline.Maximum = video.NaturalDuration.TimeSpan.TotalSeconds;
                previewReady = true;
                playbackBar.Visibility = Visibility.Visible;
                SyncTimeline();
            }
        };
        video.MediaEnded += (s, e) => { SetPlayback(false); video.Position = TimeSpan.Zero; SyncTimeline(); };
        video.MediaFailed += async (s, e) => await PreviewFallback();
        screen.Children.Add(video);
        empty = Label("Your compressed video will appear here", 17, Muted);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.TextAlignment = TextAlignment.Center;
        empty.TextWrapping = TextWrapping.Wrap;
        screen.Children.Add(empty);
    }

    private void SelectPreset(int index)
    {
        selected = index;
        custom = null;
        RefreshPreset();
    }

    private Preset ActivePreset { get { return custom ?? profiles[selected]; } }

    private void RefreshPreset()
    {
        for (int i = 0; i < cards.Length; i++)
        {
            bool active = custom == null && i == selected;
            cards[i].Background = Color(active ? "#361923" : "#211C24");
            cards[i].BorderBrush = Color(active ? "#D6344D" : "#382F3A");
        }
        advanced.Content = custom == null ? "Advanced settings…" : "Advanced settings · active";
        detail.Text = ActivePreset.Detail;
    }

    private void EditAdvanced()
    {
        var current = ActivePreset;
        var dialog = new Window {
            Owner = this, Title = "Advanced settings", WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height, Width = 460, ResizeMode = ResizeMode.NoResize,
            Background = Color("#18151B"), ShowInTaskbar = false, FontFamily = FontFamily
        };
        var form = new StackPanel { Margin = new Thickness(24) };
        dialog.Content = form;
        form.Children.Add(Label("Encoding options", 20));
        var heights = new[] { 0, 360, 480, 540, 720, 1080, 1440, 2160 };
        var sizes = new[] { "Original", "360p", "480p", "540p", "720p", "1080p", "1440p", "2160p" };
        var speeds = new[] { "veryfast", "fast", "medium", "slow", "slower" };
        var size = Options(form, "Maximum height", sizes, Array.IndexOf(heights, current.Height));
        var codec = Options(form, "Codec", new[] { "H.265 / HEVC", "H.264 / AVC" }, current.Codec == "x264" ? 1 : 0);
        var speed = Options(form, "Encoding speed", speeds, Array.IndexOf(speeds, current.Speed));
        var rate = Options(form, "Rate control", new[] { "Constant quality (RF)", "Average video bitrate (kbps)" },
            current.Bitrate > 0 ? 1 : 0);
        var value = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13,
            Padding = new Thickness(7, 0, 7, 0), Background = Color("#242026"), Foreground = Ink,
            BorderBrush = Color("#494049"), CaretBrush = Ink };
        form.Children.Add(value);
        var hint = Label("", 12, Muted);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 8, 0, 14);
        form.Children.Add(hint);
        Action updateRate = () => {
            bool qualityMode = rate.SelectedIndex == 0;
            value.Text = (qualityMode ? current.Rf : current.Bitrate > 0 ? current.Bitrate : 1200)
                .ToString(CultureInfo.InvariantCulture);
            hint.Text = qualityMode ? "Lower RF keeps more detail and usually makes a larger file. Enter 0–51."
                : "Enter an average video bitrate of 50–100,000 kbps. Audio adds to the final size.";
        };
        rate.SelectionChanged += (s, e) => updateRate();
        updateRate();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelEdit = Button("Cancel", false);
        cancelEdit.IsCancel = true;
        actions.Children.Add(cancelEdit);
        var apply = Button("Apply", true);
        apply.Margin = new Thickness(8, 0, 0, 0);
        apply.IsDefault = true;
        actions.Children.Add(apply);
        form.Children.Add(actions);
        apply.Click += (s, e) => {
            int number;
            bool average = rate.SelectedIndex == 1;
            if (!int.TryParse(value.Text, out number) ||
                (average ? number < 50 || number > 100000 : number < 0 || number > 51))
            {
                hint.Text = average ? "Enter a whole number from 50 to 100,000." : "Enter a whole number from 0 to 51.";
                value.Focus();
                return;
            }
            custom = new Preset("Custom", sizes[size.SelectedIndex], "Custom · " + sizes[size.SelectedIndex],
                heights[size.SelectedIndex], 0, average ? current.Rf : number, 64, speeds[speed.SelectedIndex],
                codec.SelectedIndex == 1 ? "x264" : "x265", average ? number : 0);
            RefreshPreset();
            dialog.DialogResult = true;
        };
        dialog.ShowDialog();
    }

    private static ComboBox Options(Panel panel, string name, string[] entries, int selectedIndex)
    {
        var title = Label(name, 13, Muted);
        title.Margin = new Thickness(0, 13, 0, 5);
        panel.Children.Add(title);
        var list = new ComboBox { ItemsSource = entries, SelectedIndex = Math.Max(0, selectedIndex),
            Height = 30, FontSize = 13 };
        System.Windows.Automation.AutomationProperties.SetName(list, name);
        panel.Children.Add(list);
        return list;
    }

    private void PickFile()
    {
        var dialog = new OpenFileDialog { Title = "Choose videos", Multiselect = true,
            Filter = "Video|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.mts|All files|*.*" };
        if (dialog.ShowDialog(this) == true) ChooseFiles(dialog.FileNames);
    }

    private void ChooseFiles(string[] paths)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            if (File.Exists(path) && seen.Add(path)) files.Add(path);
        if (files.Count == 0) return;
        foreach (var oldJob in batch) oldJob.Cancellation.Dispose();
        batch.Clear();
        batchRows.Children.Clear();
        batchPanel.Visibility = Visibility.Collapsed;
        LoadFile(files[0]);
        compress.Content = files.Count > 1 ? "Compress " + files.Count + " Videos" : "Compress Video";
        if (files.Count == 1) return;
        filename.Text = files.Count + " videos selected";
        filename.ToolTip = null;
        foreach (var source in files) AddBatchRow(source);
        batchPanel.Visibility = Visibility.Visible;
    }

    private void AddBatchRow(string source)
    {
        var job = new BatchJob { Source = source, State = Label("Queued", 12, Muted),
            Meter = new ProgressBar { Height = 4, Maximum = 100, Foreground = Red, Background = Color("#44323D") },
            Action = Button("Cancel", false) };
        var row = new DockPanel { Margin = new Thickness(2, 0, 5, 6) };
        job.Action.Height = 31;
        job.Action.Width = 80;
        DockPanel.SetDock(job.Action, Dock.Right);
        row.Children.Add(job.Action);
        var detail = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var name = Label(Path.GetFileName(source), 13);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = Path.GetFileName(source);
        detail.Children.Add(name);
        detail.Children.Add(job.State);
        detail.Children.Add(job.Meter);
        row.Children.Add(detail);
        job.Action.Click += (s, e) => {
            if (job.Output != null)
            {
                if (!batchRunning && !busy) ShowBatchResult(job);
                return;
            }
            job.Cancellation.Cancel();
            job.State.Text = "Cancelled";
            job.Action.IsEnabled = false;
        };
        batch.Add(job);
        batchRows.Children.Add(row);
    }

    private async Task EncodeBatch()
    {
        if (batchRunning || busy || !compress.IsEnabled) return;
        try { EnsureJobDirectory(); }
        catch (IOException) { status.Text = "Couldn’t create the temporary output folder."; return; }
        catch (UnauthorizedAccessException) { status.Text = "Couldn’t access the temporary output folder."; return; }
        var profile = ActivePreset;
        batchRunning = true;
        pick.IsEnabled = presetList.IsEnabled = advanced.IsEnabled = compress.IsEnabled = false;
        status.Text = "Batch running · up to two videos at a time";
        try
        {
            using (var slots = new SemaphoreSlim(2))
            {
                var tasks = new List<Task>();
                foreach (var job in batch) tasks.Add(EncodeBatchJob(job, slots, profile));
                await Task.WhenAll(tasks);
            }
        }
        finally
        {
            batchRunning = false;
            pick.IsEnabled = presetList.IsEnabled = advanced.IsEnabled = true;
        }
        BatchJob first = null;
        int completed = 0;
        foreach (var job in batch)
        {
            if (job.Output == null) continue;
            job.Action.IsEnabled = true;
            if (first == null) first = job;
            completed++;
        }
        if (first != null)
        {
            ShowBatchResult(first);
            PlayCompletionSound();
        }
        status.Text = completed + " of " + batch.Count + " videos ready";
    }

    private async Task EncodeBatchJob(BatchJob job, SemaphoreSlim slots, Preset profile)
    {
        var token = job.Cancellation.Token;
        bool acquired = false;
        try
        {
            await slots.WaitAsync(token);
            acquired = true;
            job.State.Text = "Encoding · 0%";
            var output = Path.Combine(jobDir, Guid.NewGuid().ToString("N") + ".mp4");
            var report = new Progress<double>(value => {
                if (job.Output != null || token.IsCancellationRequested || !job.Action.IsEnabled || value <= job.Meter.Value) return;
                job.Meter.Value = value;
                job.State.Text = "Encoding · " + Math.Floor(value) + "%";
            });
            await Task.Run(() => Transcode(job.Source, output, profile.Codec, profile, token, report, true));
            token.ThrowIfCancellationRequested();
            job.Output = output;
            job.CodecName = profile.CodecName;
            job.Meter.Value = 100;
            job.State.Text = "Ready · 100%";
            job.Action.Content = "Preview";
        }
        catch (OperationCanceledException) { job.State.Text = "Cancelled"; }
        catch (Exception) { job.State.Text = "Encoding failed"; }
        finally
        {
            job.Action.IsEnabled = false;
            if (acquired) slots.Release();
        }
    }

    private void ShowBatchResult(BatchJob job)
    {
        ResetPreview();
        input = job.Source;
        filename.Text = Path.GetFileName(input);
        filename.ToolTip = input;
        result = job.Output;
        previewCopy = null;
        save.IsEnabled = true;
        resultInfo.Text = (new FileInfo(result).Length / 1000000.0).ToString("0.##") + " MB  ·  " + job.CodecName;
        empty.Visibility = Visibility.Visible;
        status.Text = "Ready to save";
        OpenPreview(result);
    }

    private void EnsureJobDirectory()
    {
        if (jobDir != null) return;
        var path = Path.Combine(Path.GetTempPath(), "ROTH-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        jobDir = path;
    }

    private void LoadFile(string path)
    {
        ResetPreview();
        empty.Text = "Your compressed video will appear here";
        empty.Visibility = Visibility.Visible;
        save.IsEnabled = false;
        result = null;
        resultInfo.Text = "";
        progress.Visibility = Visibility.Collapsed;
        input = path;
        filename.Text = Path.GetFileName(path);
        filename.ToolTip = path;
        compress.IsEnabled = true;
        status.Text = "Ready to compress";
    }

    private async Task Encode()
    {
        if (busy || input == null) return;
        SetBusy(true, "Compression · 0%");
        ResetPreview();
        completionSound.Close();
        save.IsEnabled = false;
        resultInfo.Text = "";
        result = null;
        previewCopy = null;
        empty.Text = "Compressing video…";
        empty.Visibility = Visibility.Visible;
        try
        {
            EnsureJobDirectory();
            var output = Path.Combine(jobDir, Guid.NewGuid().ToString("N") + ".mp4");
            var profile = ActivePreset;
            var source = input;
            var token = jobCancellation.Token;
            var reporting = TrackProgress("Compression", token);
            await Task.Run(() => Transcode(source, output, profile.Codec, profile, token, reporting));
            token.ThrowIfCancellationRequested();
            result = output;
            resultInfo.Text = (new FileInfo(result).Length / 1000000.0).ToString("0.##") + " MB  ·  " + profile.CodecName;
            save.IsEnabled = true;
            status.Text = "100% · Ready to save";
            PlayCompletionSound();
            OpenPreview(result);
        }
        catch (OperationCanceledException)
        {
            empty.Text = "Compression cancelled";
            status.Text = "You can start compression again.";
        }
        catch (Exception error)
        {
            empty.Text = "Compression failed";
            status.Text = error.Message;
        }
        finally
        {
            SetBusy(false, null);
            if (result != null && !busy)
            {
                progress.Value = 100;
                progress.Visibility = Visibility.Visible;
            }
        }
    }

    private async Task PreviewFallback()
    {
        if (result == null) return;
        if (busy)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (busy || result == null) return;
        }
        if (previewCopy != null)
        {
            empty.Text = "Windows couldn’t open the preview";
            empty.Visibility = Visibility.Visible;
            status.Text = "You can still save the compressed file.";
            return;
        }
        previewCopy = Path.Combine(jobDir, Guid.NewGuid().ToString("N") + ".mp4");
        string source = result, destination = previewCopy;
        SetBusy(true, "Preview · 0%");
        try
        {
            var token = jobCancellation.Token;
            var reporting = TrackProgress("Preview", token);
            await Task.Run(() => Transcode(source, destination, "x264",
                new Preset("", "", "", 540, 960, 24, 64, "ultrafast"), token, reporting));
            token.ThrowIfCancellationRequested();
            OpenPreview(destination);
            status.Text = "Playing a compatible preview. Save As keeps your selected format.";
        }
        catch (OperationCanceledException)
        {
            empty.Text = "Preview cancelled";
            status.Text = "You can still save the compressed file.";
        }
        catch (Exception)
        {
            empty.Text = "Preview unavailable";
            empty.Visibility = Visibility.Visible;
            status.Text = "You can still save the compressed file.";
        }
        finally { SetBusy(false, null); }
    }

    private static void Transcode(string source, string target, string codec, Preset profile,
        CancellationToken token, IProgress<double> reporting, bool limitThreads = false)
    {
        var exe = EncoderPath;
        if (!File.Exists(exe)) throw new IOException("HandBrakeCLI is missing from the ROTH folder.");
        var rate = profile.Bitrate > 0 ? " -b " + profile.Bitrate : " -q " + profile.Rf;
        var arguments = "-i \"" + source + "\" -o \"" + target + "\" -f av_mp4 -e " + codec
            + " --encoder-preset " + profile.Speed + rate + " -a 1 -E av_aac -B "
            + profile.Audio + " -6 stereo -R 48 -r 30 --pfr -O --no-markers --crop 0:0:0:0 --no-metadata";
        if (profile.Height > 0) arguments += " -Y " + profile.Height;
        if (profile.Width > 0) arguments += " -X " + profile.Width;
        if (limitThreads) arguments += codec == "x265" ? " -x pools=2:frame-threads=2" : " -x threads=2";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var process = Process.Start(new ProcessStartInfo(exe, arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            using (token.Register(() => {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) {}
                catch (System.ComponentModel.Win32Exception) {}
            }))
            {
                process.OutputDataReceived += (s, e) => ReportEncoderLine(e.Data, reporting);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 || !File.Exists(target) || new FileInfo(target).Length == 0)
                    throw new IOException("HandBrake couldn’t process this file.");
            }
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) {} catch (UnauthorizedAccessException) {}
            throw;
        }
    }

    private static void ReportEncoderLine(string line, IProgress<double> reporting)
    {
        if (line == null || reporting == null) return;
        var match = EncodeProgress.Match(line);
        int task, total;
        double percent;
        if (match.Success && int.TryParse(match.Groups[1].Value, out task)
            && int.TryParse(match.Groups[2].Value, out total) && task >= 1 && total >= task
            && double.TryParse(match.Groups[3].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out percent))
        {
            reporting.Report(Math.Max(0, Math.Min(99, ((task - 1) * 100.0 + percent) / total)));
        }
    }

    private IProgress<double> TrackProgress(string stage, CancellationToken token)
    {
        return new Progress<double>(value => {
            if (jobCancellation == null || jobCancellation.Token != token || token.IsCancellationRequested
                || value <= progress.Value) return;
            progress.Value = value;
            status.Text = stage + " · " + Math.Floor(value).ToString(CultureInfo.CurrentCulture) + "%";
        });
    }

    private void Save()
    {
        if (result == null) return;
        var dialog = new SaveFileDialog { Filter = "MP4 video|*.mp4", AddExtension = true,
            DefaultExt = ".mp4", FileName = Path.GetFileNameWithoutExtension(input) + "_ROTH.mp4" };
        if (dialog.ShowDialog(this) != true) return;
        if (string.Equals(dialog.FileName, input, StringComparison.OrdinalIgnoreCase))
        {
            status.Text = "Choose a different name to keep the original.";
            return;
        }
        try { File.Copy(result, dialog.FileName, true); status.Text = "File saved"; }
        catch (IOException) { status.Text = "Couldn’t save. Try another name or folder."; }
        catch (UnauthorizedAccessException) { status.Text = "Access to this folder is denied."; }
    }

    private void SetBusy(bool value, string message)
    {
        if (value) jobCancellation = new CancellationTokenSource();
        else if (jobCancellation != null)
        {
            jobCancellation.Dispose();
            jobCancellation = null;
        }
        busy = value;
        cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        compress.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        cancel.IsEnabled = value;
        pick.IsEnabled = presetList.IsEnabled = advanced.IsEnabled = !value;
        compress.IsEnabled = !value && input != null && batch.Count == 0;
        if (value) progress.Value = 0;
        progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (message != null) status.Text = message;
    }

    private void SetPlayback(bool value)
    {
        playing = value;
        if (value)
        {
            video.Play();
            playbackClock.Start();
        }
        else
        {
            video.Pause();
            playbackClock.Stop();
        }
        play.Content = value ? "Ⅱ  Pause" : "▶  Play";
    }

    private void ResetPreview()
    {
        playbackClock.Stop();
        previewReady = playing = false;
        video.Close();
        video.Visibility = Visibility.Hidden;
        play.Visibility = playbackBar.Visibility = Visibility.Collapsed;
    }

    private void OpenPreview(string path)
    {
        empty.Text = "Preparing preview…";
        video.Visibility = Visibility.Visible;
        video.Source = new Uri(path);
        video.Pause();
    }

    private void SyncTimeline()
    {
        if (!previewReady) return;
        updatingTimeline = true;
        if (!timeline.IsMouseCaptureWithin) timeline.Value = video.Position.TotalSeconds;
        updatingTimeline = false;
        UpdateTimecode();
    }

    private void UpdateTimecode()
    {
        timecode.Text = FormatTime(timeline.Value) + " / " + FormatTime(timeline.Maximum);
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void PlayCompletionSound()
    {
        var cue = Path.Combine(Root, "roth-complete.mp3");
        if (!File.Exists(cue))
        {
            System.Media.SystemSounds.Asterisk.Play();
            return;
        }
        completionSound.Open(new Uri(cue));
        completionSound.Play();
    }

    private FrameworkElement Mark(double size)
    {
        var letter = Label("R", size * .55, Red);
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * .23),
            Background = Color("#361923"), Child = letter };
    }

    private static string EncoderPath { get { return Path.Combine(Root, "HandBrakeCLI", "HandBrakeCLI.exe"); } }
    private Button Button(string text, bool primary)
    {
        return new Button { Content = text, Height = 44, Padding = new Thickness(13, 0, 13, 0),
            Foreground = Ink, Background = Color(primary ? "#A91C36" : "#241D26"),
            BorderBrush = Color(primary ? "#CC3B50" : "#3A303C"), Cursor = Cursors.Hand,
            Template = buttons, FontSize = 13, FontWeight = FontWeights.SemiBold };
    }
    private static TextBlock Label(string text, double size, Brush brush = null)
    {
        return new TextBlock { Text = text, FontSize = size, Foreground = brush ?? Ink,
            VerticalAlignment = VerticalAlignment.Center, FontWeight = size >= 16 ? FontWeights.SemiBold : FontWeights.Normal };
    }
    private static Color ColorValue(string value) { return (Color)ColorConverter.ConvertFromString(value); }
    private static SolidColorBrush Color(string value) { return new SolidColorBrush(ColorValue(value)); }

    private sealed class Preset
    {
        public readonly string Name, Size, Speed, Detail, Codec;
        public readonly int Height, Width, Rf, Audio, Bitrate;
        public string CodecName { get { return Codec == "x264" ? "H.264" : "HEVC"; } }
        public Preset(string name, string size, string note, int height, int width, int rf, int audio,
            string speed, string codec = "x265", int bitrate = 0)
        {
            Name = name; Size = size; Height = height; Width = width; Rf = rf; Audio = audio; Speed = speed;
            Codec = codec; Bitrate = bitrate;
            var rate = bitrate > 0 ? bitrate + " kbps" : "RF " + rf;
            Detail = note + "\n" + CodecName + " · " + speed + " · " + rate + " · AAC " + audio + " kbps";
        }
    }

    private sealed class BatchJob
    {
        public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        public string Source, Output, CodecName;
        public TextBlock State;
        public ProgressBar Meter;
        public Button Action;
    }
}
