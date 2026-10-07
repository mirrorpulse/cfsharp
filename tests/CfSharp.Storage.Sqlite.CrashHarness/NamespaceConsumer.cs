internal static class NamespaceConsumer
{
    internal static int Run(string source, string target, string signal, string action)
    {
        using EventWaitHandle ready = EventWaitHandle.OpenExisting(signal);
        using EventWaitHandle go = EventWaitHandle.OpenExisting(signal + "-go");
        using EventWaitHandle attempted = EventWaitHandle.OpenExisting(signal + "-attempted");
        using EventWaitHandle completed = EventWaitHandle.OpenExisting(signal + "-completed");
        ready.Set();
        if (!go.WaitOne(TimeSpan.FromSeconds(15)))
        {
            return 2;
        }

        attempted.Set();
        try
        {
            switch (action)
            {
                case "rename": Directory.Move(source, target); break;
                case "delete": File.Delete(source); break;
                case "edit": File.WriteAllText(source, "independent edit"); break;
                case "create": File.WriteAllText(target, "independent child"); break;
                default: return 3;
            }

            return 0;
        }
        catch (IOException exception)
        {
            return exception.HResult & 0xffff;
        }
        finally
        {
            completed.Set();
        }
    }
}
