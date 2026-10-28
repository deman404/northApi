using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using northApi.services;

namespace northApi.components;

public partial class ResponseViewer : UserControl
{
    private HttpResponseResult? response;
    private string displayed = "";
    private HeaderRow[] headerRows = [];
    private static readonly Regex JsonTokens = new("\"(?:\\\\.|[^\"\\\\])*\"(?=\\s*:)|\"(?:\\\\.|[^\"\\\\])*\"|\\b(?:true|false|null)\\b|-?\\b\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Brush KeyColor = ColorBrush("#86CFE0");
    private static readonly Brush StringColor = ColorBrush("#C8D99D");
    private static readonly Brush NumberColor = ColorBrush("#E7B87F");
    private static readonly Brush LiteralColor = ColorBrush("#BAA4E8");

    public ResponseViewer() => InitializeComponent();
    private static Brush ColorBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    public void ShowLoading() => ShowState("Sending request…", "Waiting for the server to respond.", true, false);
    public void ShowError(string message) => ShowState("Request could not complete", message, false, true);

    private void ShowState(string title, string hint, bool loading, bool error)
    {
        response = null;
        ContentPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        EmptyImage.Visibility = error || loading ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = title;
        EmptyTitle.Foreground = error ? Brushes.Salmon : ColorBrush("#CCCCCC");
        EmptyHint.Text = hint;
        LoadingBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowResponse(HttpResponseResult result)
    {
        response = result;
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadingBar.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;
        StatusText.Text = $"{result.StatusCode} {result.ReasonPhrase}";
        var success = result.StatusCode < 400;
        StatusText.Foreground = ColorBrush(success ? "#7EE2AE" : "#F2A5A5");
        StatusBadge.Background = ColorBrush(success ? "#173128" : "#3B2327");
        TimeText.Text = result.Elapsed.TotalMilliseconds < 1000 ? $"{result.Elapsed.TotalMilliseconds:0} ms" : $"{result.Elapsed.TotalSeconds:0.00} s";
        SizeText.Text = FormatSize(result.BodyBytes) + (result.IsTruncated ? "+" : "");
        TypeText.Text = result.MediaType?.Split('/').LastOrDefault()?.ToUpperInvariant() ?? "TEXT";
        PreviewNotice.Visibility = result.IsTruncated ? Visibility.Visible : Visibility.Collapsed;
        headerRows = result.Headers.SelectMany(header => header.Values.Select(value => new HeaderRow(header.Name, value))).ToArray();
        HeadersTab.Header = $"Headers ({headerRows.Length})";
        HeaderFilter.Clear();
        FilterHeaders();
        ResultTabs.SelectedIndex = 0;
        RenderBody();
    }

    private static string FormatSize(long size) => size >= 1024 * 1024 ? $"{size / (1024d * 1024):0.##} MiB" : size >= 1024 ? $"{size / 1024d:0.##} KiB" : $"{size:N0} bytes";

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (CodeView is not null && response is not null) RenderBody();
    }

    private void RenderBody()
    {
        if (response is null) return;
        displayed = response.Body;
        var json = false;
        if (PrettyMode.IsChecked == true && !response.IsTruncated &&
            (response.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
             response.Body.TrimStart().StartsWith('{') || response.Body.TrimStart().StartsWith('[')))
        {
            try
            {
                using var document = JsonDocument.Parse(response.Body);
                displayed = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
                json = true;
            }
            catch (JsonException) { }
        }
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        // Limit highlighting work; the full bounded preview remains selectable.
        if (json && displayed.Length <= 256 * 1024)
        {
            var offset = 0;
            try
            {
                foreach (Match match in JsonTokens.Matches(displayed))
                {
                    if (match.Index > offset) paragraph.Inlines.Add(new Run(displayed[offset..match.Index]));
                    var end = match.Index + match.Length;
                    var suffix = displayed.AsSpan(end).TrimStart();
                    var brush = match.Value.StartsWith('"') ? (suffix.StartsWith(":".AsSpan()) ? KeyColor : StringColor)
                        : char.IsDigit(match.Value[0]) || match.Value[0] == '-' ? NumberColor : LiteralColor;
                    paragraph.Inlines.Add(new Run(match.Value) { Foreground = brush });
                    offset = end;
                }
                if (offset < displayed.Length) paragraph.Inlines.Add(new Run(displayed[offset..]));
            }
            catch (RegexMatchTimeoutException) { paragraph.Inlines.Clear(); paragraph.Inlines.Add(new Run(displayed)); }
        }
        else paragraph.Inlines.Add(new Run(displayed.Length == 0 ? "(empty response body)" : displayed));
        CodeView.Document.Blocks.Clear();
        CodeView.Document.Blocks.Add(paragraph);
        var lines = displayed.Split('\n');
        LineNumbers.Text = string.Join(Environment.NewLine, Enumerable.Range(1, lines.Length));
        CodeView.Document.PageWidth = Math.Clamp(lines.Max(line => line.Length) * 8d + 24, 300, 1_000_000);
        CodeView.ScrollToHome();
        GutterScroll.ScrollToTop();
    }

    private void Code_ScrollChanged(object sender, ScrollChangedEventArgs e) => GutterScroll?.ScrollToVerticalOffset(CodeView.VerticalOffset);
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (HeaderTable is not null) FilterHeaders(); }
    private void FilterHeaders() => HeaderTable.ItemsSource = headerRows.Where(row => row.Name.Contains(HeaderFilter.Text, StringComparison.OrdinalIgnoreCase) || row.Value.Contains(HeaderFilter.Text, StringComparison.OrdinalIgnoreCase)).ToArray();

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(displayed);
            CopyButton.Content = "Copied";
            await Task.Delay(1200);
            CopyButton.Content = "Copy";
        }
        catch (ExternalException) { MessageBox.Show("Clipboard is busy. Please try again.", "Copy response"); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (response is null) return;
        var extension = response.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ? "json" : "txt";
        var dialog = new SaveFileDialog { FileName = $"response.{extension}", Filter = "Response text|*." + extension + "|All files|*.*", DefaultExt = extension };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, response.Body, new UTF8Encoding(false)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { MessageBox.Show(northApi.services.SafeDiagnostics.LocalError, "Unable to save response"); }
    }

    private sealed record HeaderRow(string Name, string Value);
}
