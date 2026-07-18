using System.Reflection;
using System.Windows;

namespace MessagingServerManager.App;

public partial class SplashWindow : Window
{
    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(SplashWindow), new PropertyMetadata("Starting…"));

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string VersionText { get; } = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    public SplashWindow()
    {
        InitializeComponent();
    }
}
