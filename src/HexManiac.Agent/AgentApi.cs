using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HavenSoft.HexManiac.Agent {
   public static class AgentApi {
      public const int SchemaVersion = 1;

      public static async Task<ToolResponse<MapResult>> GetMap(string romPath, string mapName) {
         return await Execute(romPath, mapName, (viewPort, map) => {
            var layout = map.GetLayout();
            if (layout == null) throw new InvalidOperationException("Map has no layout.");
            var events = map.EventGroup;
            if (events == null) throw new InvalidOperationException("Map has no event data.");
            return new MapResult(
               map.MapID,
               layout.GetValue("width"),
               layout.GetValue("height"),
               map.GetMapModel().GetValue("layoutID"),
               new EventCounts(events.Objects.Count, events.Warps.Count, events.Scripts.Count, events.Signposts.Count)
            );
         });
      }

      public static async Task<ToolResponse<IReadOnlyList<Dictionary<string, object>>>> ListEvents(string romPath, string mapName, string type) {
         return await Execute(romPath, mapName, (viewPort, map) => {
            var events = map.EventGroup;
            if (events == null) throw new InvalidOperationException("Map has no event data.");
            var normalized = (type ?? "all").Trim().ToLowerInvariant();
            var result = new List<Dictionary<string, object>>();
            if (normalized is "all" or "object" or "objects") result.AddRange(ToObjects(events.Objects));
            if (normalized is "all" or "warp" or "warps") result.AddRange(ToWarps(events.Warps));
            if (normalized is "all" or "script" or "scripts") result.AddRange(ToScripts(events.Scripts));
            if (normalized is "all" or "signpost" or "signposts") result.AddRange(ToSignposts(events.Signposts));
            if (result.Count == 0 && normalized is not ("all" or "object" or "objects" or "warp" or "warps" or "script" or "scripts" or "signpost" or "signposts")) {
               throw new ArgumentException($"Unknown event type: {type}");
            }
            return (IReadOnlyList<Dictionary<string, object>>)result;
         });
      }

      public static async Task<ToolResponse<FreeValueResult>> FindFreeFlag(string romPath) {
         return await Execute(romPath, null, (viewPort, _) => {
            var parser = viewPort.Tools.CodeTool.ScriptParser;
            var usage = Flags.GetScriptUsage(viewPort.Model, parser);
            var used = new HashSet<int>(usage.ItemFlags);
            used.UnionWith(usage.TrainerFlags);
            return new FreeValueResult(Flags.NextFreeFlag(viewPort.Model, used));
         });
      }

      public static async Task<ToolResponse<FreeValueResult>> FindFreeVar(string romPath) {
         return await Execute(romPath, null, (viewPort, _) => {
            var usage = Flags.GetScriptUsage(viewPort.Model, viewPort.Tools.CodeTool.ScriptParser);
            return new FreeValueResult(Flags.NextFreeVariable(viewPort.Model, usage.Variables));
         });
      }

      private static async Task<ToolResponse<T>> Execute<T>(string romPath, string mapName, Func<ViewPort, BlockMapViewModel, T> action) {
         try {
            if (!File.Exists(romPath)) return ToolResponse<T>.Failure("rom_not_found", $"ROM not found: {romPath}");
            var viewPort = await LoadViewPort(romPath);
            await viewPort.InitializationWorkload;
            BlockMapViewModel map = null;
            if (mapName != null) {
               if (viewPort.MapEditor == null) return ToolResponse<T>.Failure("map_editor_unavailable", "The ROM does not expose a valid map editor.");
               viewPort.Goto.Execute(mapName);
               map = viewPort.MapEditor.PrimaryMap;
               if (map == null) return ToolResponse<T>.Failure("map_not_found", $"Map not found: {mapName}");
            }
            return ToolResponse<T>.Success(action(viewPort, map));
         } catch (ArgumentException ex) {
            return ToolResponse<T>.Failure("invalid_argument", ex.Message);
         } catch (Exception ex) {
            return ToolResponse<T>.Failure("tool_error", ex.Message);
         }
      }

      private static async Task<ViewPort> LoadViewPort(string romPath) {
         var singletons = new Singletons();
         var metadataPath = Path.ChangeExtension(romPath, ".toml");
         var fileSystem = new StubFileSystem {
            MetadataFor = fileName => string.Equals(fileName, romPath, StringComparison.OrdinalIgnoreCase) && File.Exists(metadataPath)
               ? File.ReadAllLines(metadataPath)
               : Array.Empty<string>()
         };
         var model = new HardcodeTablesModel(singletons, await File.ReadAllBytesAsync(romPath), new StoredMetadata(fileSystem.MetadataFor(romPath) ?? Array.Empty<string>()));
         return new ViewPort(romPath, model, InstantDispatch.Instance, singletons, new MapTutorialsViewModel(), fileSystem);
      }

      private static IReadOnlyList<Dictionary<string, object>> ToObjects(IEnumerable<ObjectEventViewModel> source) => source.Select(item => new Dictionary<string, object> {
         ["type"] = "object", ["id"] = item.ObjectID, ["x"] = item.X, ["y"] = item.Y, ["elevation"] = item.Elevation,
         ["graphics"] = item.Graphics, ["scriptAddress"] = NullablePointer(item.ScriptAddress), ["flag"] = item.Flag,
      }).ToList();

      private static IReadOnlyList<Dictionary<string, object>> ToWarps(IEnumerable<WarpEventViewModel> source) => source.Select(item => new Dictionary<string, object> {
         ["type"] = "warp", ["id"] = item.WarpID > 0 ? item.WarpID : null, ["x"] = item.X, ["y"] = item.Y,
         ["elevation"] = item.Elevation, ["bank"] = item.Bank, ["map"] = item.Map,
      }).ToList();

      private static IReadOnlyList<Dictionary<string, object>> ToScripts(IEnumerable<ScriptEventViewModel> source) => source.Select(item => new Dictionary<string, object> {
         ["type"] = "script", ["x"] = item.X, ["y"] = item.Y, ["elevation"] = item.Elevation, ["trigger"] = item.Trigger,
         ["index"] = item.Index, ["scriptAddress"] = NullablePointer(item.ScriptAddress),
      }).ToList();

      private static IReadOnlyList<Dictionary<string, object>> ToSignposts(IEnumerable<SignpostEventViewModel> source) {
         var result = new List<Dictionary<string, object>>();
         foreach (var item in source) {
            var output = new Dictionary<string, object> {
               ["type"] = "signpost", ["x"] = item.X, ["y"] = item.Y, ["elevation"] = item.Elevation, ["kind"] = item.Kind,
            };
            if (item.Kind < 5) output["scriptAddress"] = NullablePointer(item.Pointer);
            else if (item.Kind <= 7) {
               output["itemId"] = item.ItemID; output["hiddenItemId"] = item.HiddenItemID;
               output["quantity"] = item.Quantity; output["isUnderFoot"] = item.IsUnderFoot;
            } else if (item.Kind == 8) output["secretBaseId"] = item.ArgText;
            result.Add(output);
         }
         return result;
      }

      private static object NullablePointer(int value) => value == Pointer.NULL ? null : value;

      public sealed record ToolResponse<T>(int SchemaVersion, bool Ok, T Result, ToolError Error) {
         public static ToolResponse<T> Success(T result) => new(AgentApi.SchemaVersion, true, result, null);
         public static ToolResponse<T> Failure(string code, string message) => new(AgentApi.SchemaVersion, false, default, new ToolError(code, message));
      }
      public sealed record ToolError(string Code, string Message);
      public sealed record MapResult(int MapId, int Width, int Height, int LayoutId, EventCounts EventCounts);
      public sealed record EventCounts(int Objects, int Warps, int Scripts, int Signposts);
      public sealed record FreeValueResult(int Value);
   }
}
