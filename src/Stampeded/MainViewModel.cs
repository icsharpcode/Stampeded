using Dock.Model.Controls;
using Dock.Model.Core;

using Stampeded.Docking;

namespace Stampeded;

public class MainViewModel
{
	public IRootDock Layout { get; }

	public MainViewModel()
	{
		var factory = new StampededDockFactory();
		Layout = factory.CreateLayout();
		factory.InitLayout(Layout);
	}
}
