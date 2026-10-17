using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace HavenSoft.HexManiac.Agent {
   internal static class Program {
      private static readonly JsonSerializerOptions JsonOptions = new() {
         WriteIndented = true,
      };

      private static async Task<int> Main(string[] args) {
         if (args.Length == 4 && string.Equals(args[0], "call", StringComparison.OrdinalIgnoreCase)) {
         Directory.SetCurrentDirectory(AppContext.BaseDirectory);
         return await Call(args[1], Path.GetFullPath(args[2]), args[3]);
      }
         if (args.Length < 2 || (string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase) && args.Length != 3) ||
             (string.Equals(args[0], "list-maps", StringComparison.OrdinalIgnoreCase) && args.Length != 2)) {
            Console.Error.WriteLine("Usage: agent inspect <rom> <map>");
            Console.Error.WriteLine("       agent list-maps <rom>");
            Console.Error.WriteLine("       agent call <tool> <rom> <json args>");
            return 2;
         }

         var romPath = Path.GetFullPath(args[1]);
         if (!File.Exists(romPath)) {
            Console.Error.WriteLine($"ROM not found: {romPath}");
            return 2;
         }

         Directory.SetCurrentDirectory(AppContext.BaseDirectory);
         try {
            if (string.Equals(args[0], "list-maps", StringComparison.OrdinalIgnoreCase)) {
               foreach (var map in await ListMaps(romPath)) Console.WriteLine(map);
            } else if (string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase)) {
               var result = await Inspect(romPath, args[2]);
               Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            } else {
               Console.Error.WriteLine("Unknown command.");
               return 2;
            }
            return 0;
         } catch (Exception ex) {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
         }
      }

      private static async Task<int> Call(string tool, string romPath, string json) {
            try {
               using var document = JsonDocument.Parse(json);
               var root = document.RootElement;
               var map = root.TryGetProperty("map", out var mapValue) ? mapValue.GetString() : null;
               switch (tool.ToLowerInvariant()) {
                  case "get_map": return WriteResponse(await AgentApi.GetMap(romPath, map));
                  case "list_events": return WriteResponse(await AgentApi.ListEvents(romPath, map, root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : "all"));
                  case "find_free_flag": return WriteResponse(await AgentApi.FindFreeFlag(romPath));
                  case "find_free_var": return WriteResponse(await AgentApi.FindFreeVar(romPath));
                  default:
                     Console.WriteLine(JsonSerializer.Serialize(AgentApi.ToolResponse<object>.Failure("unknown_tool", $"Unknown tool: {tool}"), JsonOptions));
                     return 1;
               }
            } catch (Exception ex) {
               var response = AgentApi.ToolResponse<object>.Failure("invalid_request", ex.Message);
               Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
               return 1;
            }
         }

      private static int WriteResponse<T>(AgentApi.ToolResponse<T> response) {
         Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
         return response.Ok ? 0 : 1;
      }

      private static async Task<InspectionResult> Inspect(string romPath, string map) {
         var viewPort = await LoadViewPort(romPath);
         await viewPort.InitializationWorkload;
         if (viewPort.MapEditor == null) throw new InvalidOperationException("The ROM does not expose a valid map editor.");

         viewPort.Goto.Execute(map);
         var primaryMap = viewPort.MapEditor.PrimaryMap;
         if (primaryMap == null) throw new InvalidOperationException($"Map not found: {map}");

         var events = primaryMap.EventGroup;
         if (events == null) throw new InvalidOperationException($"Map has no event data: {map}");

         return new InspectionResult(
            romPath,
            map,
            primaryMap.MapID,
            new EventLists(
               ToObjects(events.Objects),
               ToWarps(events.Warps),
               ToScripts(events.Scripts),
               ToSignposts(events.Signposts)
            )
         );
      }

      private static async Task<IReadOnlyList<string>> ListMaps(string romPath) {
         var viewPort = await LoadViewPort(romPath);
         await viewPort.InitializationWorkload;
         var banks = viewPort.Model.GetTableModel(HardcodeTablesModel.MapBankTable);
         if (banks == null) throw new InvalidOperationException("The ROM does not expose a map bank table.");

         var result = new List<string>();
         for (var bank = 0; bank < banks.Count; bank++) {
            var maps = banks[bank].GetSubTable("maps");
            if (maps == null) continue;
            for (var map = 0; map < maps.Count; map++) {
               result.Add($"maps.bank{bank}.{BlockMapViewModel.MapIDToText(viewPort.Model, bank, map)}");
            }
         }
         return result;
      }

      private static async Task<ViewPort> LoadViewPort(string romPath) {
         var singletons = new Singletons();
         var metadataPath = Path.ChangeExtension(romPath, ".toml");
         var fileSystem = new StubFileSystem {
            MetadataFor = fileName => string.Equals(fileName, romPath, StringComparison.OrdinalIgnoreCase) && File.Exists(metadataPath)
               ? File.ReadAllLines(metadataPath)
               : Array.Empty<string>()
         };
         var model = new HardcodeTablesModel(
            singletons,
            await File.ReadAllBytesAsync(romPath),
            new StoredMetadata(fileSystem.MetadataFor(romPath) ?? Array.Empty<string>())
         );
         return new ViewPort(
            romPath,
            model,
            InstantDispatch.Instance,
            singletons,
            new MapTutorialsViewModel(),
            fileSystem
         );
      }

      private static IReadOnlyList<Dictionary<string, object>> ToObjects(IEnumerable<ObjectEventViewModel> source) {
         var result = new List<Dictionary<string, object>>();
         foreach (var item in source) {
            result.Add(new() {
               ["type"] = "object",
               ["id"] = item.ObjectID,
               ["x"] = item.X,
               ["y"] = item.Y,
               ["elevation"] = item.Elevation,
               ["graphics"] = item.Graphics,
               ["scriptAddress"] = NullablePointer(item.ScriptAddress),
               ["flag"] = item.Flag,
            });
         }
         return result;
      }

      private static IReadOnlyList<Dictionary<string, object>> ToWarps(IEnumerable<WarpEventViewModel> source) {
         var result = new List<Dictionary<string, object>>();
         foreach (var item in source) {
            result.Add(new() {
               ["type"] = "warp",
               ["id"] = item.WarpID > 0 ? item.WarpID : null,
               ["x"] = item.X,
               ["y"] = item.Y,
               ["elevation"] = item.Elevation,
               ["bank"] = item.Bank,
               ["map"] = item.Map,
            });
         }
         return result;
      }

      private static IReadOnlyList<Dictionary<string, object>> ToScripts(IEnumerable<ScriptEventViewModel> source) {
         var result = new List<Dictionary<string, object>>();
         foreach (var item in source) {
            result.Add(new() {
               ["type"] = "script",
               ["x"] = item.X,
               ["y"] = item.Y,
               ["elevation"] = item.Elevation,
               ["trigger"] = item.Trigger,
               ["index"] = item.Index,
               ["scriptAddress"] = NullablePointer(item.ScriptAddress),
            });
         }
         return result;
      }

      private static IReadOnlyList<Dictionary<string, object>> ToSignposts(IEnumerable<SignpostEventViewModel> source) {
         var result = new List<Dictionary<string, object>>();
         foreach (var item in source) {
            var output = new Dictionary<string, object> {
               ["type"] = "signpost",
               ["x"] = item.X,
               ["y"] = item.Y,
               ["elevation"] = item.Elevation,
               ["kind"] = item.Kind,
            };
            if (item.Kind < 5) output["scriptAddress"] = NullablePointer(item.Pointer);
            else if (item.Kind <= 7) {
               output["itemId"] = item.ItemID;
               output["hiddenItemId"] = item.HiddenItemID;
               output["quantity"] = item.Quantity;
               output["isUnderFoot"] = item.IsUnderFoot;
            } else if (item.Kind == 8) {
               output["secretBaseId"] = item.ArgText;
            }
            result.Add(output);
         }
         return result;
      }

      private static object NullablePointer(int value) => value == Pointer.NULL ? null : value;

      private sealed record InspectionResult(string Rom, string Map, int MapId, EventLists Events);
      private sealed record EventLists(
         IReadOnlyList<Dictionary<string, object>> Objects,
         IReadOnlyList<Dictionary<string, object>> Warps,
         IReadOnlyList<Dictionary<string, object>> Scripts,
         IReadOnlyList<Dictionary<string, object>> Signposts
      );
   }
}
