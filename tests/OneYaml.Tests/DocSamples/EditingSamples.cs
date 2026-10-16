#region EditIndexers
using OneYaml;
#endregion
using Xunit;

namespace DocSamples.Editing;

[Collection(nameof(DocSample))]
public class EditingSamples
{
    [Fact]
    public void EditIndexers() => DocSample.Run(nameof(EditIndexers), () =>
    {
        #region EditIndexers
        using var doc = YamlDocument.Parse("""
            # Deployment for the web tier
            spec:
              replicas: 2        # raised by the release job
              image: web:1.4
              ports: [80, 443]
            """);

        YamlNode spec = doc.Root["spec"];
        spec["replicas"] = 3;              // replaces the value, keeps the comment
        spec["paused"] = false;            // a new key goes at the end of its mapping
        spec["ports"][2] = 8443;           // an index equal to the count appends
        spec["image"].Remove();
        Console.WriteLine(doc);
        // # Deployment for the web tier
        // spec:
        //   replicas: 3        # raised by the release job
        //   ports: [80, 443, 8443]
        //   paused: false
        #endregion
    });

    [Fact]
    public void EditPointers() => DocSample.Run(nameof(EditPointers), () =>
    {
        #region EditPointers
        using var doc = YamlDocument.Parse("""
            metadata:
              annotations:
                example.com/owner: web-team
            spec:
              containers:
                - name: web
                  image: web:1.4
            """);

        doc.Set("/spec/containers/0/image", "web:1.5");
        doc.Set("/metadata/annotations/example.com~1owner", "platform"); // ~1 stands for '/'
        doc.Add("/spec/containers/-", new Dictionary<string, object?> { ["name"] = "proxy", ["image"] = "envoy:1.30" });

        Console.WriteLine(doc.Get<string>("/spec/containers/1/name")); // proxy
        Console.WriteLine(doc.Contains("/spec/volumes"));            // False
        if (doc.TryGet("/spec/containers/0/image", out string? image))
        {
            Console.WriteLine(image); // web:1.5
        }

        Console.WriteLine(doc);
        // metadata:
        //   annotations:
        //     example.com/owner: platform
        // spec:
        //   containers:
        //     - name: web
        //       image: web:1.5
        //     - name: proxy
        //       image: envoy:1.30
        #endregion
    });

    [Fact]
    public void EditStyles() => DocSample.Run(nameof(EditStyles), () =>
    {
        #region EditStyles
        using var doc = YamlDocument.Parse("""
            plain: one
            single: 'one'
            double: "one"
            literal: |
              one
            """);

        doc.Set("/plain", "two words");        // still plain
        doc.Set("/single", "it's");            // still single-quoted, the quote doubled
        doc.Set("/double", "tab\there");       // still double-quoted, with an escape
        doc.Set("/literal", "no final break"); // still literal, with the strip indicator
        Console.Write(doc);
        // plain: two words
        // single: 'it''s'
        // double: "tab\there"
        // literal: |-
        //   no final break

        doc.Set("/plain", "a: b");             // plain cannot hold ': ', so it is quoted
        doc.Set("/single", "two\nlines\n");    // single quotes cannot keep the break, so double quotes
        Console.WriteLine(doc.Root["plain"].Style);  // SingleQuoted
        Console.WriteLine(doc.Root["single"].Style); // DoubleQuoted
        #endregion
    });

    [Fact]
    public void EditLookalikes() => DocSample.Run(nameof(EditLookalikes), () =>
    {
        #region EditLookalikes
        const string Src = "enabled: x\nversion: x\n";

        using var core = YamlDocument.Parse(Src);
        core.Set("/enabled", "yes");      // a string under YAML 1.2, so it stays plain
        core.Set("/version", "1.10");     // a number under YAML 1.2, so it is quoted
        Console.Write(core);
        // enabled: yes
        // version: '1.10'

        using var both = YamlDocument.Parse(Src, new YamlDocumentOptions { QuoteYaml11Lookalikes = true });
        both.Set("/enabled", "yes");      // quoted, so a YAML 1.1 reader also sees a string
        Console.WriteLine(both.Root["enabled"].Style); // SingleQuoted
        #endregion
    });

    [Fact]
    public void EditAliases() => DocSample.Run(nameof(EditAliases), () =>
    {
        #region EditAliases
        using var doc = YamlDocument.Parse("""
            defaults: &defaults
              cpu: 2
            prod: *defaults
            """);

        Console.WriteLine(doc.Get<int>("/prod/cpu")); // 2

        try
        {
            doc.Set("/prod/cpu", 4); // would change 'defaults' too
        }
        catch (YamlException e) when (e.Category == YamlErrorCategory.RefusedAlias)
        {
            Console.WriteLine(e.Message);
            // The edit would write through an alias and change every place that shares the anchored node.
        }

        doc.Set("/prod", new Dictionary<string, object?> { ["cpu"] = 4 }); // replacing the alias itself is fine
        Console.WriteLine(doc);
        // defaults: &defaults
        //   cpu: 2
        // prod:
        //   cpu: 4
        #endregion
    });

    [Fact]
    public void EditReading() => DocSample.Run(nameof(EditReading), () =>
    {
        #region EditReading
        using var doc = YamlDocument.Parse("""
            name: web
            replicas: 2
            labels: {tier: frontend, team: core}
            """);

        YamlNode root = doc.Root;
        Console.WriteLine(root["replicas"].GetValue<int>() + 1); // 3
        Console.WriteLine(root["labels"].Style);                  // Flow

        foreach (var (key, value) in root["labels"].Entries)
        {
            Console.WriteLine($"{key.GetValue<string>()} = {value.GetValue<string>()}");
            // tier = frontend
            // team = core
        }

        if (!root.TryGetValue("owner", out YamlNode? owner))
        {
            Console.WriteLine("no owner"); // no owner
        }

        var service = root.Deserialize<Service>()!;
        Console.WriteLine($"{service.Name}: {service.Labels.Count} labels"); // web: 2 labels
        #endregion
    });

    [Fact]
    public void EditStream() => DocSample.Run(nameof(EditStream), () =>
    {
        #region EditStream
        using var doc = YamlDocument.Parse("""
            kind: Service
            name: web
            ---
            kind: Deployment
            name: web
            replicas: 2
            """);

        Console.WriteLine(doc.Documents.Count); // 2
        doc.Set("/replicas", 3, document: 1);
        doc.Documents[0]["name"] = "web-svc";
        Console.WriteLine(doc);
        // kind: Service
        // name: web-svc
        // ---
        // kind: Deployment
        // name: web
        // replicas: 3
        #endregion
    });
}

#region EditReading
record Service(string Name, int Replicas, Dictionary<string, string> Labels);
#endregion
