// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace IceScreenTouch.Api.Tests;

/// <summary>The specification against what a running server does.</summary>
public class OpenApiServerTests : ServerTestBase
{
    [Fact]
    public async Task TheServedDocumentIsTheEmbeddedOneAndNeedsNoToken()
    {
        var response = await SendAsync(HttpMethod.Get, "/v1/openapi.json", token: null);

        Assert.Equal(200, (int)response.StatusCode);
        var served = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True(JsonNode.DeepEquals(SpecFiles.LoadEmbeddedJson(), served));
    }

    [Fact]
    public async Task EveryOperationNeedsTheTokenExactlyWhenTheSpecSaysSo()
    {
        var spec = SpecFiles.LoadYaml();
        var globalSecurity = spec["security"]!.AsArray().Count > 0;
        foreach (var (path, item) in spec["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                var secured = operation!["security"] is JsonArray own ? own.Count > 0 : globalSecurity;
                var response = await SendAsync(new HttpMethod(method.ToUpperInvariant()), path, token: null);

                Assert.Equal(secured, (int)response.StatusCode == 401);
            }
        }
    }
}
