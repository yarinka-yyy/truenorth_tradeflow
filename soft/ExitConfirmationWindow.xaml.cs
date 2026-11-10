using System.Windows;

namespace TrueNorth.TradeFlow;

public partial class ExitConfirmationWindow : Window
{
    public ExitConfirmationWindow()
    {
        InitializeComponent();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
