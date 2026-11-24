using Microsoft.Maui.Controls;

namespace NureTimetable.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainPage = new NavigationPage(new MainPage());
    }
}