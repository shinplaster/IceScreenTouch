// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>
/// The examples and schemas of docs/api/openapi.yaml against a running server: what the document shows as a request
/// body is accepted, and what the server answers has the documented shape.
/// </summary>
public class OpenApiExampleTests : ServerTestBase
{
    /// <summary>One theory case for every request body example of the specification: <c>method</c>, <c>path</c>, example name.</summary>
    public static IEnumerable<object[]> RequestExamples()
    {
        var spec = SpecFiles.LoadYaml();
        foreach (var (path, item) in spec["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                if (JsonBodyOf(operation!) is not { } content)
                {
                    continue;
                }

                foreach (var name in ExamplesOf(content).Keys)
                {
                    yield return [method.ToUpperInvariant(), path, name];
                }
            }
        }
    }

    [Fact]
    public void EveryRequestBodyOfTheSpecificationHasAtLeastOneExample()
    {
        var spec = SpecFiles.LoadYaml();
        var without = new List<string>();
        var checkedBodies = 0;
        foreach (var (path, item) in spec["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                if (operation!["requestBody"] is null)
                {
                    continue;
                }

                checkedBodies++;
                if (JsonBodyOf(operation) is not { } content || ExamplesOf(content).Count == 0)
                {
                    without.Add($"{method.ToUpperInvariant()} {path}");
                }
            }
        }

        Assert.True(checkedBodies >= 9, "the specification should have the nine request bodies");
        Assert.True(without.Count == 0, "no example for: " + string.Join(", ", without));
    }

    [Theory]
    [MemberData(nameof(RequestExamples))]
    public async Task EveryRequestBodyExampleIsAcceptedByTheServer(string method, string path, string name)
    {
        var spec = SpecFiles.LoadYaml();
        var operation = spec["paths"]![path]![method.ToLowerInvariant()]!;
        var content = JsonBodyOf(operation)!;
        var body = ExamplesOf(content)[name];

        // The example is valid against the schema the document gives for the body (light check, see SchemaCheck).
        var problems = SchemaCheck.Validate(spec, content["schema"]!, body);
        Assert.True(problems.Count == 0, $"the example '{name}' of {method} {path} does not match its schema: {string.Join("; ", problems)}");

        var response = await Drive(SendAsync(new HttpMethod(method), path, body.ToJsonString()));

        // Anything but a refusal of the request itself is fine (a precondition of the fake host is not the point here).
        if ((int)response.StatusCode == 400)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.Fail($"the server answered 400 to the example '{name}' of {method} {path}: {text}\nbody: {body.ToJsonString()}");
        }

        Assert.NotEqual(400, (int)response.StatusCode);
        Assert.NotEqual(415, (int)response.StatusCode);
    }

    [Fact]
    public async Task TheStatusResponseMatchesTheStatusSchema()
    {
        var spec = SpecFiles.LoadYaml();
        var response = await GetAsync("/v1/status");
        Assert.Equal(200, (int)response.StatusCode);
        var status = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var problems = SchemaCheck.ValidateNamed(spec, "Status", status);
        Assert.True(problems.Count == 0, string.Join("; ", problems));

        // The same with no picture and no device: the nullable members are null, not missing.
        Host.Picture = null;
        var bare = JsonNode.Parse(await (await GetAsync("/v1/status")).Content.ReadAsStringAsync())!;
        Assert.Null(bare["picture"]);
        Assert.True(bare.AsObject().ContainsKey("picture"));
        problems = SchemaCheck.ValidateNamed(spec, "Status", bare);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public async Task TheOtherJsonAnswersMatchTheirSchemas()
    {
        var spec = SpecFiles.LoadYaml();

        var rotation = JsonNode.Parse(await (await GetAsync("/v1/rotation")).Content.ReadAsStringAsync())!;
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "Rotation", rotation));

        var typed = await PostDriven("/v1/keys/type", """{ "text": "hello" }""");
        Assert.Equal(200, (int)typed.StatusCode);
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "TypeTextResponse", JsonNode.Parse(await typed.Content.ReadAsStringAsync())));

        var cancel = await PostAsync("/v1/cancel");
        Assert.Equal(200, (int)cancel.StatusCode);
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "CancelResponse", JsonNode.Parse(await cancel.Content.ReadAsStringAsync())));

        var empty = await PostDriven("/v1/touch/tap", """{ "x": 0.5, "y": 0.5 }""");
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "Empty", JsonNode.Parse(await empty.Content.ReadAsStringAsync())));
    }

    [Fact]
    public async Task TheScreenshotMatchesItsDocumentedParametersHeadersAndErrors()
    {
        var spec = SpecFiles.LoadYaml();
        var operation = spec["paths"]!["/v1/screenshot"]!["get"]!;

        // The query parameters and their limits are the ones the server enforces.
        var parameters = operation["parameters"]!.AsArray().ToDictionary(p => p!["name"]!.GetValue<string>(), p => p!["schema"]!);
        Assert.Equal(["format", "maxSize", "quality"], parameters.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["png", "jpeg"], parameters["format"]["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(ScreenshotOptions.MinMaxSize, parameters["maxSize"]["minimum"]!.GetValue<long>());
        Assert.Equal(ScreenshotOptions.MaxMaxSize, parameters["maxSize"]["maximum"]!.GetValue<long>());
        Assert.Equal(1, parameters["quality"]["minimum"]!.GetValue<long>());
        Assert.Equal(100, parameters["quality"]["maximum"]!.GetValue<long>());
        Assert.Equal(ScreenshotOptions.DefaultQuality, parameters["quality"]["default"]!.GetValue<long>());

        // The headers of a real answer are the documented ones, with documented values.
        Host.Frame = TestPictures.Quadrants(30, 20, landscape: true, rotation: Rotation.R90);
        var ok = await GetAsync("/v1/screenshot");
        Assert.Equal(200, (int)ok.StatusCode);
        Assert.Contains(ok.Content.Headers.ContentType!.MediaType!, operation["responses"]!["200"]!["content"]!.AsObject().Select(c => c.Key));
        foreach (var (name, header) in operation["responses"]!["200"]!["headers"]!.AsObject())
        {
            Assert.True(ok.Headers.TryGetValues(name, out var values), $"the answer has no {name}");
            var value = values!.Single();
            Assert.Empty(SchemaCheck.Validate(spec, header!["schema"]!, JsonValue.Create(value)));
            if (header["schema"]!["pattern"] is { } pattern)
            {
                Assert.Matches(pattern.GetValue<string>(), value);
            }
        }

        // The refusals the document lists are the ones the server answers, with the documented Retry-After.
        var documented = operation["responses"]!.AsObject().Select(r => r.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["200", "400", "401", "403", "409", "429", "500"], documented);
        for (var i = 0; i < 10; i++)
        {
            await GetAsync("/v1/screenshot");
        }

        var limited = await GetAsync("/v1/screenshot");
        var problem = await AssertProblem(limited, ApiErrorCodes.RateLimited);
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "Problem", JsonNode.Parse(problem.GetRawText())));
        var retry = spec["components"]!["responses"]!["RateLimited"]!["headers"]!["Retry-After"]!;
        Assert.Empty(SchemaCheck.Validate(spec, retry["schema"]!, JsonValue.Create(limited.Headers.GetValues("Retry-After").Single())));
    }

    [Fact]
    public async Task AnErrorAnswerMatchesTheProblemSchema()
    {
        var spec = SpecFiles.LoadYaml();
        var response = await SendAsync(HttpMethod.Post, "/v1/touch/tap", """{ "x": 5, "y": 5 }""");

        await AssertProblem(response, ApiErrorCodes.InvalidRequest);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "Problem", problem));
    }

    [Fact]
    public void TheCheckerFindsWhatIsWrong()
    {
        // The light validator itself: a test that always passed would prove nothing.
        var spec = SpecFiles.LoadYaml();

        Assert.NotEmpty(SchemaCheck.ValidateNamed(spec, "Status", JsonNode.Parse("{}")));
        Assert.NotEmpty(SchemaCheck.ValidateNamed(spec, "Rotation", JsonNode.Parse("""{ "landscape": 45, "portrait": 0 }""")));
        Assert.NotEmpty(SchemaCheck.ValidateNamed(spec, "TapRequest", JsonNode.Parse("""{ "x": 1, "y": 1, "space": "pixel" }"""))); // pixel needs ref
        Assert.NotEmpty(SchemaCheck.ValidateNamed(spec, "TapRequest", JsonNode.Parse("""{ "x": 1, "y": 1, "extra": 1 }""")));
        Assert.NotEmpty(SchemaCheck.ValidateNamed(spec, "TapRequest", JsonNode.Parse("""{ "x": "1", "y": 1 }""")));
        Assert.NotEmpty(SchemaCheck.ValidateNamed(spec, "TapRequest", JsonNode.Parse("""{ "x": 1, "y": 1, "count": 11 }""")));
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "TapRequest", JsonNode.Parse("""{ "x": 1, "y": 1, "space": "pixel", "ref": { "width": 1, "height": 1 } }""")));
    }

    private static JsonObject? JsonBodyOf(JsonNode operation) =>
        operation["requestBody"]?["content"]?["application/json"] as JsonObject;

    /// <summary>The examples of a media type object: the entries of <c>examples</c> (their <c>value</c>) and <c>example</c>.</summary>
    private static Dictionary<string, JsonNode> ExamplesOf(JsonObject content)
    {
        var found = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (content["examples"] is JsonObject named)
        {
            foreach (var (name, example) in named)
            {
                found[name] = example!["value"]!.DeepClone();
            }
        }

        if (content["example"] is { } single)
        {
            found["example"] = single.DeepClone();
        }

        return found;
    }
}
