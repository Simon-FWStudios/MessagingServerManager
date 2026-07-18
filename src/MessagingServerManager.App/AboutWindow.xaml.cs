using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MessagingServerManager.App;

public partial class AboutWindow : Window
{
    public string VersionText { get; }
    public string AboutText { get; }

    public AboutWindow()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText = "Version " + version;
        AboutText = LoadAboutText().Replace("{{Version}}", version, StringComparison.Ordinal);
        InitializeComponent();
        AboutDocument.Document = MarkdownDocumentRenderer.Render(AboutText);
    }

    static string LoadAboutText()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MessagingServerManager.App.Assets.About.md");
        if (stream is null) return "Messaging Server Manager";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}

static class MarkdownDocumentRenderer
{
    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13.5,
            Foreground = new SolidColorBrush(Color.FromRgb(20, 32, 51))
        };
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List list = null!;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                list = null!;
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                list = null!;
                document.Blocks.Add(Heading(line[4..], 16, new Thickness(0, 16, 0, 6)));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                list = null!;
                document.Blocks.Add(Heading(line[3..], 18, new Thickness(0, 18, 0, 7)));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                list = null!;
                document.Blocks.Add(Heading(line[2..], 22, new Thickness(0, 0, 0, 10)));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                list ??= AddList(document);
                list.ListItems.Add(new ListItem(Paragraph(line[2..], new Thickness(0, 2, 0, 2))));
            }
            else
            {
                list = null!;
                document.Blocks.Add(Paragraph(line, new Thickness(0, 4, 0, 10)));
            }
        }

        return document;
    }

    static Paragraph Heading(string text, double size, Thickness margin)
    {
        var paragraph = Paragraph(text, margin);
        paragraph.FontSize = size;
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.Foreground = new SolidColorBrush(Color.FromRgb(7, 63, 116));
        return paragraph;
    }

    static Paragraph Paragraph(string text, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin, LineHeight = 20 };
        AddInlineMarkdown(paragraph, text);
        return paragraph;
    }

    static List AddList(FlowDocument document)
    {
        var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(18, 0, 0, 10), Padding = new Thickness(12, 0, 0, 0) };
        document.Blocks.Add(list);
        return list;
    }

    static void AddInlineMarkdown(Paragraph paragraph, string text)
    {
        var remaining = text;
        while (remaining.Length > 0)
        {
            var start = remaining.IndexOf("**", StringComparison.Ordinal);
            if (start < 0)
            {
                paragraph.Inlines.Add(new Run(remaining));
                return;
            }

            if (start > 0) paragraph.Inlines.Add(new Run(remaining[..start]));
            var end = remaining.IndexOf("**", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                paragraph.Inlines.Add(new Run(remaining[start..]));
                return;
            }

            paragraph.Inlines.Add(new Run(remaining[(start + 2)..end]) { FontWeight = FontWeights.SemiBold });
            remaining = remaining[(end + 2)..];
        }
    }
}
