using Avalonia.Controls;

namespace Stampeded;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		InitializeComponent();
		DataContext = new MainViewModel();
	}
}
