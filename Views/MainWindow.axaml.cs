using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DvdRipper.ViewModels;

namespace DvdRipper.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Closing += (s, e) => (DataContext as MainWindowViewModel)?.CancelCurrentRip();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}