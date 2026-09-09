using AGProfileHub.Core;

internal static class AgentProbeTests
{
    public static Task ClassifierAsync()
    {
        var cases = new (string Json, int Exit, string Expected)[]
        {
            ("{\"error\":null,\"response\":{}}", 0, "success"),
            ("{\"error\":null,\"response\":{}}", 1, "remote-error"),
            ("{\"error\":\"secret not found\",\"response\":null}", 1, "remote-error"),
            ("{\"error\":\"csrf secret\",\"response\":null}", 0, "remote-error"),
            ("{\"response\":{}}", 0, "invalid-envelope"),
            ("[]", 0, "invalid-envelope"),
            ("not found", 1, "invalid-envelope"),
            ("", 0, "invalid-envelope"),
        };
        foreach (var item in cases)
            if (AgentRuntimeProbe.Classify(item.Json, item.Exit) != item.Expected)
                throw new Exception("Agent envelope classification mismatch.");
        return Task.CompletedTask;
    }
}
