using System;
using System.IO;
using System.Threading;
using System.Linq;
using RouteLens;

internal static class SubscriptionQuotaChecks
{
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }

    private static void Main(string[] args)
    {
        var provider = ClashConnectionReader.ParseProviderQuotas(@"{""providers"":{
          ""A"":{""updatedAt"":""2026-10-10T10:00:00+02:00"",""subscriptionInfo"":{""Upload"":5,""Download"":7,""Total"":20,""Expire"":0}},
          ""B"":{""subscriptionInfo"":{""Upload"":4,""Download"":7,""Total"":10}},
          ""C"":{""subscriptionInfo"":{""Upload"":1,""Total"":10}},
          ""D"":{""subscriptionInfo"":{""Upload"":9223372036854775807,""Download"":1,""Total"":12}},
          ""E"":{""subscriptionInfo"":{""Upload"":1,""Download"":1,""Total"":0}},
          ""F"":{""subscriptionInfo"":null}}}");
        Check(provider.Count == 2 && !provider.Any(p => p.IsCurrent), "Valid separate providers without guessing");
        Check(provider[0].UsedBytes == 12 && provider[0].RemainingBytes == 8 && provider[0].UpdatedAtUtc.Value.Hour == 8, "Provider arithmetic/date");
        Check(!provider[0].ExpiresAtUtc.HasValue && provider[1].RemainingBytes == 0, "Zero expiry and exhausted quota");
        Check(ClashConnectionReader.ParseProviderQuotas("{}").Count == 0, "Missing provider data");
        bool malformed = false;
        try { ClashConnectionReader.ParseProviderQuotas("{broken"); } catch (ArgumentException) { malformed = true; }
        Check(malformed, "Malformed controller data");
        const string profiles = @"# Profiles Config for Clash Verge
items:
- uid: earlier
  type: remote
  extra:
    upload: 3
    download: 10
    total: 110
- uid: current-key
  type: remote
  name: Example
  url: https://invalid.example/?token=hidden
  extra:
    upload: 25
    download: 20
    total: 50
    expire: 1735920000
  option:
    upload: 2000
  updated: 1725920000
- uid: auxiliary
  type: script
  extra:
    upload: 1
    download: 2
    total: 50
current: current-key
";
        var quotas = ClashConnectionReader.ParseVergeQuotas(new StringReader(profiles));
        Check(quotas.Count == 2 && !quotas[0].IsCurrent && quotas[1].IsCurrent, "Exact active profile selection");
        Check(quotas[1].RemainingBytes == 5 && quotas[1].UpdatedAtUtc.Value.Kind == DateTimeKind.Utc &&
            quotas[1].ExpiresAtUtc.HasValue && quotas[1].Id != "current-key", "Local quota, UTC timestamps and public ID");
        Check(ClashConnectionReader.ParseVergeQuotas(new StringReader(profiles.Replace("current: current-key", "current: absent"))).All(q => !q.IsCurrent), "No active fallback");
        Check(ClashConnectionReader.ParseVergeQuotas(new StringReader("items: []")).Count == 0, "Missing local data");
        if (Array.IndexOf(args, "--local") >= 0)
        {
            var local = ClashConnectionReader.ReadVergeQuotas();
            var current = local.SingleOrDefault(q => q.IsCurrent);
            Console.WriteLine("Local subscriptions with quota: " + local.Count + "; current: " + (current == null ? 0 : 1));
            if (current != null) Console.WriteLine("Current totals: used=" + current.UsedBytes + " remaining=" + current.RemainingBytes +
                " total=" + current.TotalBytes + " updatedUtc=" + (current.UpdatedAtUtc.HasValue ? current.UpdatedAtUtc.Value.ToString("u") : "unknown"));
            using (var reader = new ClashConnectionReader())
            {
                reader.GetSubscriptionQuotas();
                SubscriptionQuota[] cached = new SubscriptionQuota[0];
                for (int i = 0; i < 65 && cached.Length == 0; i++)
                {
                    Thread.Sleep(100);
                    cached = reader.GetSubscriptionQuotas();
                }
                if (current != null) Check(cached.Any(q => q.Id == current.Id && q.RemainingBytes == current.RemainingBytes), "Local controller failure must preserve local profile quota");
                Console.WriteLine("Application cache subscriptions: " + cached.Length);
            }
        }
        Console.WriteLine("Subscription quota checks passed.");
    }
}
