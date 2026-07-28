using System.Text.Json;

namespace EngrCAD.Mcp;

/// <summary>
/// Output schemas for the tools that return JSON payloads — hand-written because the
/// tool methods return the protocol's <c>CallToolResult</c> directly (so the SDK
/// cannot infer a schema from the return type), and set on
/// <c>McpServerToolCreateOptions.OutputSchema</c> with <c>UseStructuredContent</c> so
/// clients can consume <c>structuredContent</c> without parsing text blocks.
/// <para>The schemas describe the payloads <see cref="SceneTools"/> builds; the
/// structured-content round-trip test holds them together (a payload field the schema
/// does not know is legal — objects stay open — but a declared required field the
/// payload stops producing fails the test).</para>
/// <para><c>screenshot</c> deliberately has none: its result is an image content
/// block, which structured content does not model.</para>
/// </summary>
internal static class ToolSchemas
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>list_tabs: the tab table.</summary>
    internal static readonly JsonElement ListTabs = Parse("""
        {
          "type": "object",
          "required": ["generation", "tabs"],
          "properties": {
            "generation": { "type": "integer", "description": "Bumps on reload and on every model edit; compare to detect stale reads." },
            "tabs": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["name", "parts", "assemblies", "distinctParts", "instances"],
                "properties": {
                  "name": { "type": "string" },
                  "parts": { "type": "integer" },
                  "assemblies": { "type": "integer" },
                  "distinctParts": { "type": "integer" },
                  "instances": { "type": "integer" }
                }
              }
            }
          }
        }
        """);

    /// <summary>list_parts: the part table.</summary>
    internal static readonly JsonElement ListParts = Parse("""
        {
          "type": "object",
          "required": ["generation", "parts"],
          "properties": {
            "generation": { "type": "integer" },
            "parts": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["name", "tab", "kind", "displayMode", "annotations",
                             "hasConstructionTree", "exactBrep", "instances", "paths"],
                "properties": {
                  "name": { "type": "string" },
                  "tab": { "type": "string" },
                  "kind": { "type": "string" },
                  "displayMode": { "type": "string" },
                  "annotations": { "type": "integer" },
                  "hasConstructionTree": { "type": "boolean", "description": "True for parts an assistant can edit with set_param (feature history) or inspect step by step." },
                  "exactBrep": { "type": "boolean" },
                  "instances": { "type": "integer" },
                  "paths": { "type": "array", "items": { "type": "string" } },
                  "color": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 }
                }
              }
            }
          }
        }
        """);

    /// <summary>describe_part: one part's measured facts + construction record.</summary>
    internal static readonly JsonElement DescribePart = Parse("""
        {
          "type": "object",
          "required": ["generation", "name", "tab", "kind", "faces", "vertices", "closed",
                       "area", "bounds", "localBounds", "position", "paths"],
          "properties": {
            "generation": { "type": "integer" },
            "name": { "type": "string" },
            "tab": { "type": "string" },
            "kind": { "type": "string" },
            "displayMode": { "type": "string" },
            "exactBrep": { "type": "boolean" },
            "faces": { "type": "integer" },
            "vertices": { "type": "integer" },
            "closed": { "type": "boolean" },
            "volume": { "type": ["number", "null"], "description": "Null when the mesh is open." },
            "area": { "type": "number" },
            "bounds": { "$ref": "#/$defs/bounds" },
            "localBounds": { "$ref": "#/$defs/bounds" },
            "position": { "$ref": "#/$defs/point" },
            "paths": { "type": "array", "items": { "type": "string" } },
            "annotations": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["type"],
                "properties": {
                  "type": { "type": "string" },
                  "text": { "type": "string" },
                  "value": { "type": ["number", "null"] }
                }
              }
            },
            "annotationsError": { "type": "string" },
            "constructionTree": { "$ref": "#/$defs/constructionNode" }
          },
          "$defs": {
            "point": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
            "bounds": {
              "type": "object",
              "properties": {
                "empty": { "type": "boolean" },
                "min": { "$ref": "#/$defs/point" },
                "max": { "$ref": "#/$defs/point" },
                "size": { "$ref": "#/$defs/point" }
              }
            },
            "constructionNode": {
              "type": "object",
              "required": ["label", "kind", "path"],
              "properties": {
                "label": { "type": "string" },
                "kind": { "type": "string" },
                "path": { "type": "string" },
                "detail": { "type": "string" },
                "suppressed": { "type": "boolean" },
                "children": { "type": "array", "items": { "$ref": "#/$defs/constructionNode" } }
              }
            }
          }
        }
        """);

    /// <summary>export: what was written where.</summary>
    internal static readonly JsonElement Export = Parse("""
        {
          "type": "object",
          "required": ["wrote", "format"],
          "properties": {
            "wrote": {
              "description": "The file written, or the list of files for a multi-part STEP export.",
              "anyOf": [ { "type": "string" }, { "type": "array", "items": { "type": "string" } } ]
            },
            "format": { "type": "string" },
            "instances": { "type": "integer" },
            "width": { "type": "integer" },
            "height": { "type": "integer" },
            "skipped": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    /// <summary>reload: the fresh scene's headline counts.</summary>
    internal static readonly JsonElement Reload = Parse("""
        {
          "type": "object",
          "required": ["generation", "tabs", "parts", "status"],
          "properties": {
            "generation": { "type": "integer" },
            "tabs": { "type": "integer" },
            "parts": { "type": "integer" },
            "status": { "type": "string" }
          }
        }
        """);

    /// <summary>set_param / suppress_feature / unsuppress_feature: the regeneration
    /// report (one schema — the payloads differ only in which edit fields are present).</summary>
    internal static readonly JsonElement Regeneration = Parse("""
        {
          "type": "object",
          "required": ["generation", "part", "feature", "succeeded", "geometryUpdated", "features"],
          "properties": {
            "generation": { "type": "integer" },
            "part": { "type": "string" },
            "feature": { "type": "string" },
            "param": { "type": "string" },
            "value": { "description": "The value as set (set_param only)." },
            "suppressed": { "type": "boolean" },
            "succeeded": { "type": "boolean" },
            "geometryUpdated": { "type": "boolean", "description": "False when regeneration failed (the part keeps its previous geometry)." },
            "note": { "type": "string" },
            "features": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["name", "outcome", "elapsedMs"],
                "properties": {
                  "name": { "type": "string" },
                  "outcome": { "type": "string", "enum": ["applied", "cached", "suppressed", "failed", "skipped"] },
                  "elapsedMs": { "type": "number" },
                  "error": { "type": "string" }
                }
              }
            }
          }
        }
        """);
}
