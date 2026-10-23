using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using StartupHub.Core;

Console.OutputEncoding = new UTF8Encoding(false);
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
var arguments = args.ToList(); bool json = arguments.Remove("--json");
string? Take(string option)
{
    int index = arguments.IndexOf(option); if (index < 0) return null;
    if (index + 1 >= arguments.Count) throw new ArgumentException("缺少参数：" + option);
    string value = arguments[index + 1]; arguments.RemoveRange(index, 2); return value;
}
try
{
    var dataOption = Take("--data"); var appOption = Take("--app");
    if (arguments.Count == 0 || arguments.Contains("--help"))
    {
        Console.WriteLine(json ? JsonSerializer.Serialize(new CommandResponse(true, CommandApi.Help), CommandProtocol.Json) : CommandApi.Help); return 0;
    }
    var app = appOption == null ? Path.Combine(AppContext.BaseDirectory, "StartupHub.exe") : Path.GetFullPath(appOption);
    var dataRoot = dataOption == null ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(app)!, "..", "data")) : Path.GetFullPath(dataOption);
    var pipe = CommandProtocol.PipeName(dataOption != null);
    NamedPipeClientStream Client() => new(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
    var client = Client();
    try { await client.ConnectAsync(400); }
    catch (TimeoutException)
    {
        client.Dispose();
        if (!File.Exists(app)) throw new FileNotFoundException("找不到 StartupHub.exe，请将 CLI 与应用放在同一个发布目录，或使用 --app 指定程序。", app);
        Directory.CreateDirectory(dataRoot);
        var host = new TaskEntry { Path = app, WorkingDir = Path.GetDirectoryName(app)!, Arguments = ["--tray"] };
        if (dataOption != null) host.Arguments.AddRange(["--data", dataRoot]);
        using var process = NativeProcess.Launch(host, Path.Combine(dataRoot, "host.out.log"), Path.Combine(dataRoot, "host.err.log"));
        client = Client();
        try { await client.ConnectAsync(15000); }
        catch (TimeoutException) { client.Dispose(); throw new IOException("启动台已拉起但未能连接，请手动运行 " + app + " 查看是否报错。"); }
    }
    await using (client)
    {
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new CommandRequest(arguments.ToArray(), dataRoot), CommandProtocol.Json));
        string line;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            try { line = await reader.ReadLineAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new IOException("启动台超过 60 秒未返回结果，可能已无响应。"); }
        var result = line ?? throw new IOException("应用未返回结果。");
        using var document = JsonDocument.Parse(result);
        bool ok = document.RootElement.GetProperty("ok").GetBoolean();
        if (json) Console.WriteLine(result);
        else if (ok)
        {
            var value = document.RootElement.GetProperty("data");
            Console.WriteLine(value.ValueKind == JsonValueKind.String ? value.GetString() : JsonSerializer.Serialize(value, DataStore.Json));
        }
        else Console.Error.WriteLine(document.RootElement.GetProperty("error").GetString());
        return ok ? 0 : 1;
    }
}
catch (Exception ex)
{
    if (json) Console.WriteLine(JsonSerializer.Serialize(new CommandResponse(false, null, ex.Message), CommandProtocol.Json));
    else Console.Error.WriteLine(ex.Message);
    return ex is ArgumentException ? 2 : 1;
}

