// Build-time helper: renders the Foxide Helper logo into app.ico.
namespace FoxideDisk
{
    static class IconMaker
    {
        static int Main(string[] args)
        {
            Logo.WriteIco(args.Length > 0 ? args[0] : "app.ico");
            return 0;
        }
    }
}
