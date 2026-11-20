using System.Text.Json;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

if (args.Length != 2) { return 2; }
using TraceLog log = TraceLog.OpenOrConvert(args[0], new TraceLogOptions { ContinueOnError = true });
HashSet<int> hosts = [];
foreach (TraceEvent data in log.Events)
{
    if (data.PayloadNames.Contains("ModuleILPath") &&
        string.Equals(Path.GetFileName(data.PayloadByName("ModuleILPath")?.ToString()), "MirrorPulse.Host.dll", StringComparison.OrdinalIgnoreCase))
    {
        hosts.Add(data.ProcessID);
    }
}
using TraceEventDispatcher source = log.Events.GetSource();
List<object> records = [];
source.Clr.All += data =>
{
    if (!hosts.Contains(data.ProcessID) ||
        !(data.EventName.Contains("Assembly", StringComparison.OrdinalIgnoreCase) ||
          data.EventName.Contains("Exception", StringComparison.OrdinalIgnoreCase))) { return; }
    Dictionary<string, string> payload = [];
    foreach (string name in data.PayloadNames)
    {
        string value = data.PayloadByName(name)?.ToString() ?? string.Empty;
        if (name.Contains("Path", StringComparison.OrdinalIgnoreCase)) { value = Path.GetFileName(value); }
        if (name.Contains("Message", StringComparison.OrdinalIgnoreCase) && !value.Contains("RuntimeInformation", StringComparison.OrdinalIgnoreCase)) { continue; }
        payload[name] = value;
    }
    List<string> stack = [];
    for (TraceCallStack? frame = data.CallStack(); frame is not null && stack.Count < 32; frame = frame.Caller)
    {
        stack.Add(frame.CodeAddress.FullMethodName);
    }
    records.Add(new { data.ProcessID, data.ThreadID, data.TimeStampRelativeMSec, data.EventName, payload, stack });
};
source.Process();
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
return 0;
