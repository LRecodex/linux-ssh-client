using Gtk;

public static class Program
{
    public static void Main()
    {
        Application.Init();
        var win = new MainWindow();
        win.ShowAll();
        Application.Run();
    }
}
