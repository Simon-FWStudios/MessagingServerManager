using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MessagingServerManager.App;

public static class RichTextBoxLogBehavior
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(RichTextBoxLogBehavior),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.None, OnTextChanged));

    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not RichTextBox box) return;
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0), LineHeight = 18, Foreground = box.Foreground,
            FontFamily = box.FontFamily, FontSize = box.FontSize
        };
        foreach (var line in ((string?)args.NewValue ?? "").Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            AppendSeverityRuns(paragraph, line);
            document.Blocks.Add(paragraph);
        }
        box.Document = document;
        box.ScrollToEnd();
    }

    private static void AppendSeverityRuns(Paragraph paragraph, string line)
    {
        var markers = new[]
        {
            (Text: "[ERR]", Colour: "#F87171"), (Text: "[FTL]", Colour: "#F87171"),
            (Text: "[WRN]", Colour: "#FBBF24"), (Text: "[WARN]", Colour: "#FBBF24"),
            (Text: "[INF]", Colour: "#4ADE80"), (Text: "[INFO]", Colour: "#4ADE80"),
            (Text: "[DBG]", Colour: "#60A5FA"), (Text: "[DEBUG]", Colour: "#60A5FA")
        };
        var matches = markers.Select(x => (Marker: x, Index: line.IndexOf(x.Text, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.Index >= 0).OrderBy(x => x.Index).ToList();
        if (matches.Count == 0) { paragraph.Inlines.Add(new Run(line)); return; }
        var match = matches[0];
        if (match.Index > 0) paragraph.Inlines.Add(new Run(line[..match.Index]));
        paragraph.Inlines.Add(new Run(line.Substring(match.Index, match.Marker.Text.Length))
        {
            Foreground = (Brush)new BrushConverter().ConvertFromString(match.Marker.Colour)!, FontWeight = FontWeights.SemiBold
        });
        paragraph.Inlines.Add(new Run(line[(match.Index + match.Marker.Text.Length)..]));
    }
}
