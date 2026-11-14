using System.Threading;
using System.Windows;

namespace Island
{
    public partial class App : Application
    {
        static Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // не даём запустить второй остров
            _mutex = new Mutex(true, "Island_SingleInstance_Mutex", out bool created);
            if (!created) { Shutdown(); return; }
            base.OnStartup(e);
        }
    }
}
