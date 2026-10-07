using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SampleWpfApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Close();
}

public sealed class MainViewModel
{
    public string Greeting { get; set; } = "Hello";

    public bool IsBusy { get; set; }
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
}
