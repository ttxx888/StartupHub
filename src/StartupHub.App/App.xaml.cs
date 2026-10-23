using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Windows;
using StartupHub.Core;

namespace StartupHub.App;

public partial class App : Application
{
    private Mutex? mutex;
    private CancellationTokenSource shutdown = new();
    public static DataStore Store { get; private set; } = null!;
    public static TaskEngine Engine { get; private set; } = null!;
    public static string Executable => Environment.ProcessPath!;
    private string pipeName = "";
    private LoginExecution login = null!;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "启动台", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        var dataIndex = Array.IndexOf(e.Args, "--data");
        var root = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "data"));


        pipeName = CommandProtocol.PipeName(dataIndex >= 0);
        mutex = new Mutex(true, @"Local\" + pipeName, out var first);
        if (!first)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                await client.ConnectAsync(2000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                await writer.WriteLineAsync(e.Args.Contains("--autostart") ? "autostart" : "show");
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            Shutdown(); return;
        }
        try
        {
            Store = new DataStore(root);
            ThemeManager.Initialize(Store);
            var importIndex = Array.IndexOf(e.Args, "--import-legacy");
            if (importIndex >= 0 && importIndex + 1 < e.Args.Length)
            {
                Store.ReplaceConfig(DataStore.ImportLegacy(e.Args[importIndex + 1]));
                if (e.Args.Contains("--import-only")) { Shutdown(); return; }
            }
            Engine = new TaskEngine(Store);
            Engine.DiscoveryCatalogPath = LocalHtmlDiscovery.FindCatalog(AppContext.BaseDirectory);
            Engine.PortalLogin = PortalAutomation.OpenAndTryLoginAsync;
            login = new LoginExecution(Store, Engine);
            login.Log("process", e.Args.Contains("--autostart") ? "登录入口启动" : "普通启动，不执行自启清单", e.Args);
            Engine.Activity += message => login.Log("execution", message);
            var window = new MainWindow(new MainViewModel(Store, Engine));
            if (dataIndex >= 0) window.Title = "启动台 · 预览";
            MainWindow = window;
            _ = ListenAsync();
            if (!e.Args.Contains("--tray")) window.Show();
            else window.InitializeTray();
            if (e.Args.Contains("--autostart")) await login.ReceiveAsync("process-autostart", e.Args);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动台无法启动", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    private async Task ListenAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(shutdown.Token);
                _ = HandleClientAsync(server);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(300); }
        }
    }
    private async Task HandleClientAsync(NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var message = await reader.ReadLineAsync(shutdown.Token);
                if (message == "show") { login.Log("existing-instance", "普通打开，仅显示窗口"); await Dispatcher.InvokeAsync(() => ((MainWindow)MainWindow).ShowFromTray()); return; }
                if (message == "autostart")
                {
                    _ = Dispatcher.InvokeAsync(async () => await login.ReceiveAsync("existing-instance", ["--autostart", "--tray"])).Task.Unwrap(); return;
                }
                if (message == null) return;
                var request = JsonSerializer.Deserialize<CommandRequest>(message, CommandProtocol.Json)!;
                CommandResponse result;
                if (!Path.GetFullPath(request.DataRoot).TrimEnd('\\').Equals(Path.GetFullPath(Store.Root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    result = new(false, null, "指定数据目录与正在运行的实例不同，请先退出该实例。");
                else result = await Dispatcher.InvokeAsync(async () => await ((MainWindow)MainWindow).ExecuteCommandAsync(request.Args)).Task.Unwrap();
                await writer.WriteLineAsync(JsonSerializer.Serialize(result, CommandProtocol.Json));
                if (request.Args.SequenceEqual(new[] { "ui", "exit" })) await Dispatcher.InvokeAsync(() => ((MainWindow)MainWindow).ExitApplication());
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (JsonException) { }
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        shutdown.Cancel(); mutex?.Dispose();
        ThemeManager.Shutdown();
        base.OnExit(e);
    }
}

