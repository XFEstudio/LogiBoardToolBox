using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

if (OperatingSystem.IsWindows() && args.Length == 3 && args[0] == "--read-driver-target-events")
{
    var targetFolder = Path.GetFullPath(args[1]);
    var targetAddresses = args[2].Split(',').Select(s => BitConverter.GetBytes(Convert.ToUInt64(s,16))).ToArray();
    using var targetSource = new ETWTraceEventSource(Path.Combine(targetFolder,"keyboard-driver.etl"));
    var targetRecords = new List<object>();
    targetSource.AllEvents += data =>
    {
        var raw = data.EventData();
        if (targetAddresses.Any(a => raw.AsSpan().IndexOf(a) >= 0))
            targetRecords.Add(new { Time=data.TimeStamp.ToString("O"), Provider=data.ProviderGuid, Id=(int)data.ID, data.EventName, data.Version, Raw=Convert.ToHexString(raw) });
    };
    targetSource.Process();
    await File.WriteAllTextAsync(Path.Combine(targetFolder,"target-events.json"), JsonSerializer.Serialize(targetRecords,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"Matched receiver driver events: {targetRecords.Count}.");
    return 0;
}
if (OperatingSystem.IsWindows() && args.Length == 3 && args[0] is "--keyboard-driver" or "--keyboard-queues")
{
    var snapshotOnly = args[0] == "--keyboard-queues";
    var wppFolder = Path.GetFullPath(args[1]); Directory.CreateDirectory(wppFolder);
    using var wppSession = new TraceEventSession("LogiBoardKeyboardDriver-" + Guid.NewGuid().ToString("N"), Path.Combine(wppFolder,"keyboard-driver.etl"));
    wppSession.StopOnDispose = true;
    if (!snapshotOnly) wppSession.EnableProvider(new Guid("b41b0a56-4483-48ef-a772-0b007cbea8c6"), TraceEventLevel.Verbose, 0x3F);
    wppSession.EnableProvider(new Guid("47c779cd-4efd-49d7-9b10-9f16e5c25d06"), TraceEventLevel.Verbose, snapshotOnly ? 2UL : 0xFFFUL);
    try { wppSession.CaptureState(new Guid("47c779cd-4efd-49d7-9b10-9f16e5c25d06"), snapshotOnly ? 2UL : 0xFFFUL); } catch (Exception error) { Console.WriteLine(error.Message); }
    await File.WriteAllTextAsync(Path.Combine(wppFolder,"ready.json"), JsonSerializer.Serialize(new { At=DateTimeOffset.Now, InputGeneration=false, DeviceActions=false }));
    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(int.Parse(args[2]),1,30)));
    wppSession.Stop();
    Console.WriteLine("Keyboard driver status trace completed.");
    return 0;
}
if (!OperatingSystem.IsWindows() || args.Length is < 2 or > 3 || args[0] is not ("--rundown" or "--receiver-transfers")) return 2;
var transfers = args[0] == "--receiver-transfers";
var duration = args.Length == 3 ? Math.Clamp(int.Parse(args[2]), 1, 45) : 2;
var folder = Path.GetFullPath(args[1]);
Directory.CreateDirectory(folder);
var records = new ConcurrentQueue<object>();
var errors = new ConcurrentQueue<string>();
var targetDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var targetPipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var mousePipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var endpointMetadata = new ConcurrentQueue<(string Device, string Endpoint, string Pipe, bool Mouse)>();
var schemas = new ConcurrentDictionary<string, string[]>();
using var session = new TraceEventSession("LogiBoardReceiverRundown-" + Guid.NewGuid().ToString("N"));
session.StopOnDispose = true;
session.Source.AllEvents += data =>
{
    if (!transfers || data.ProviderGuid != new Guid("36DA592D-E43A-4E28-AF6F-4BC57C5A11E8") || (int)data.ID is not (22 or 25 or 28 or 29)) return;
    var raw = data.EventData();
    if (raw.Length < 40) return;
    var device = "0x" + BitConverter.ToUInt64(raw, 8).ToString("x");
    var pipe = "0x" + BitConverter.ToUInt64(raw, 16).ToString("x");
    if (targetDevices.Contains(device) && !mousePipes.Contains(pipe))
        records.Enqueue(new { Time=data.TimeStamp.ToString("O"), Provider=data.ProviderName, Event="Raw USB transfer data", Id=(int)data.ID, Version=data.Version,
            Fields=new Dictionary<string,string> { ["fid_UsbDevice"]=device, ["fid_PipeHandle"]=pipe }, RawEventData=Convert.ToHexString(raw) });
};
session.Source.Dynamic.All += data =>
{
    if (records.Count >= 20000) return;
    try
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < data.PayloadNames.Length; i++) values[data.PayloadNames[i]] = data.PayloadString(i);
        schemas.TryAdd(data.ProviderName + ":" + data.ID, data.PayloadNames);
        if (values.TryGetValue("fid_DeviceInterfacePath", out var devicePath) && devicePath.Contains("VID_046D&PID_C547", StringComparison.OrdinalIgnoreCase)
            && values.TryGetValue("fid_UsbDevice", out var hubDevice)) targetDevices.Add(hubDevice);
        if (values.TryGetValue("fid_USB_Device_Descriptor", out var descriptor) && descriptor.Contains("\"fid_idVendor\":1133") && descriptor.Contains("\"fid_idProduct\":-15033")
            && values.TryGetValue("fid_UsbDevice", out var xhciDevice)) targetDevices.Add(xhciDevice);
        if (values.TryGetValue("fid_UsbDevice", out var endpointDevice)
            && values.TryGetValue("fid_UCX_Endpoint_Descriptor", out var endpointDescriptor))
        {
            endpointMetadata.Enqueue((endpointDevice, values.GetValueOrDefault("fid_Endpoint", ""), values.GetValueOrDefault("fid_PipeHandle", ""), endpointDescriptor.Contains("\"fid_bEndpointAddress\":\"129\"")));
        }
        if (transfers && (!values.Values.Any(v => targetDevices.Contains(v) || targetPipes.Contains(v)) || values.Values.Any(mousePipes.Contains))) return;
        // Only receiver-matched transfer data is retained, never other devices' input.
        if (transfers)
            for (var i = 0; i < data.PayloadNames.Length; i++)
                if (data.PayloadValue(i) is byte[] bytes) values[data.PayloadNames[i]] = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
        records.Enqueue(new { Time = data.TimeStamp.ToString("O"), Provider = data.ProviderName, Event = data.EventName, Id = (int)data.ID, Version=data.Version, Keywords=((ulong)data.Keywords).ToString("X"), Fields = values,
            RawEventData = transfers ? Convert.ToHexString(data.EventData()) : "" });
    }
    catch (Exception error) { if (errors.Count < 10) errors.Enqueue(error.Message); }
};
var processing = Task.Run(() => session.Source.Process());
// Default and Rundown contain device metadata, without USB transfer payloads.
foreach (var provider in new[] { "Microsoft-Windows-USB-USBHUB3", "Microsoft-Windows-USB-UCX", "Microsoft-Windows-USB-USBXHCI", "Microsoft-Windows-Input-HIDCLASS" })
    session.EnableProvider(provider, TraceEventLevel.Verbose, transfers && provider.Contains("-USB-") ? 0x81C1UL : 0x8001UL);
await Task.Delay(300);
if (transfers)
{
    var metadataDeadline = DateTime.UtcNow.AddSeconds(5);
    while ((targetDevices.Count == 0 || !endpointMetadata.Any(e => targetDevices.Contains(e.Device))) && DateTime.UtcNow < metadataDeadline)
    { session.Flush(); await Task.Delay(200); }
    foreach (var endpoint in endpointMetadata.Where(e => targetDevices.Contains(e.Device)))
    {
        var pipes = endpoint.Mouse ? mousePipes : targetPipes;
        if (endpoint.Endpoint.Length != 0) pipes.Add(endpoint.Endpoint);
        if (endpoint.Pipe.Length != 0) pipes.Add(endpoint.Pipe);
    }
    if (targetDevices.Count == 0 || targetPipes.Count == 0) throw new InvalidOperationException("Receiver USB metadata was not identified.");
    foreach (var provider in new[] { "Microsoft-Windows-USB-USBHUB3", "Microsoft-Windows-USB-UCX", "Microsoft-Windows-USB-USBXHCI" })
        session.EnableProvider(provider, TraceEventLevel.Verbose, 0x81C1);
    await File.WriteAllTextAsync(Path.Combine(folder, "ready.json"), JsonSerializer.Serialize(new { At=DateTimeOffset.Now, targetDevices, targetPipes }));
}
await Task.Delay(TimeSpan.FromSeconds(duration));
var lostEvents = session.EventsLost;
session.Stop();
await processing.WaitAsync(TimeSpan.FromSeconds(10));
await File.WriteAllTextAsync(Path.Combine(folder, transfers ? "receiver-transfers.json" : "rundown.json"), JsonSerializer.Serialize(new { DeviceActions = false, TransferPayloadCollection = transfers, ReceiverOnly = transfers, TargetDevices=targetDevices, TargetPipes=targetPipes, Records = records, Schemas=schemas, Errors = errors, EventsLost = lostEvents }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"USB metadata events: {records.Count}; lost: {lostEvents}.");
return 0;
