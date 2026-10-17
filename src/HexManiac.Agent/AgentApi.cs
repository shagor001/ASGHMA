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
using System.Reflection;
using System.Text.Json.Serialization;
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

      public static async Task<ToolResponse<FreeValueResult>> FindFreeFlag(string romPath, string range = "persistent") {
         return await Execute(romPath, null, (viewPort, _) => {
            var parser = viewPort.Tools.CodeTool.ScriptParser;
            var usage = Flags.GetScriptUsage(viewPort.Model, parser);
            var used = new HashSet<int>(usage.ItemFlags);
            used.UnionWith(Flags.GetReservedFlags(viewPort.Model));
            var selected = FindFlagInRange(viewPort.Model, used, range);
            if (selected.Value < 0) throw new ArgumentException($"No free flag exists in range '{range}'.");
            return selected;
         });
      }

      public static async Task<ToolResponse<FreeValueResult>> FindFreeVar(string romPath, string range = "persistent") {
         return await Execute(romPath, null, (viewPort, _) => {
            var usage = Flags.GetScriptUsage(viewPort.Model, viewPort.Tools.CodeTool.ScriptParser);
            var used = new HashSet<int>(usage.Variables);
            used.UnionWith(Flags.GetReservedVariables(viewPort.Model));
            var selected = FindVariableInRange(viewPort.Model, used, range);
            if (selected.Value < 0) throw new ArgumentException($"No free variable exists in range '{range}'.");
            return selected;
         });
      }

      public static async Task<ToolResponse<ScriptResult>> DecompileScript(string romPath, int address) {
         return await Execute(romPath, null, (viewPort, _) => {
            var run = viewPort.Model.GetNextRun(address);
            if (run is not XSERun xse || xse.Start != address) throw new ArgumentException($"Address {address} is not the start of a script.");
            var parser = viewPort.Tools.CodeTool.ScriptParser;
            var length = parser.GetScriptSegmentLength(viewPort.Model, address);
            if (length <= 0) throw new ArgumentException($"Address {address} is not a decodable script.");
            var sections = 0;
            return new ScriptResult(address, parser.Parse(viewPort.Model, address, length, ref sections));
         });
      }

      public static async Task<ToolResponse<ConstantResult>> ListConstants(string romPath, string kind) {
         return await Execute(romPath, null, (viewPort, _) => {
            var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
            var (listName, label) = normalized switch {
               "items" => (HardcodeTablesModel.ItemsTableName, "items"),
               "species" => (HardcodeTablesModel.GetSpeciesNameTable(viewPort.Model), "species"),
               "sprites" or "object_graphics" or "object-graphics" => (HardcodeTablesModel.OverworldSprites, "sprites"),
               "movement" or "movement_types" or "movement-types" => ("movementtypes", "movement_types"),
               "flags" => (Flags.FlagListName, "flags"),
               "vars" or "variables" => (Flags.VariableListName, "vars"),
               _ => throw new ArgumentException($"Unknown constant kind: {kind}.")
            };
            var options = viewPort.Model.GetOptions(listName) ?? Array.Empty<string>();
            if (options.Count == 0 && normalized is "sprites" or "object_graphics" or "object-graphics") {
               var table = viewPort.Model.GetTableModel(listName);
               options = table == null ? Array.Empty<string>() : Enumerable.Range(0, table.Count).Select(value => value.ToString()).ToList();
            }
            var values = options.Select((name, value) => new ConstantEntry(
               value,
               string.IsNullOrWhiteSpace(name) || name == value.ToString() ? null : name,
               string.IsNullOrWhiteSpace(name) || name == value.ToString() ? "rom-value" : "reference-file-or-metadata"
            )).ToList();
            return new ConstantResult(label, listName, values);
         });
      }

      public static async Task<ToolResponse<IReadOnlyList<CommandResult>>> SearchScriptCommands(string romPath, string query) {
         return await Execute(romPath, null, (viewPort, _) => {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query cannot be empty.");
            var field = typeof(ScriptParser).GetField("engine", BindingFlags.Instance | BindingFlags.NonPublic);
            var engine = field?.GetValue(viewPort.Tools.CodeTool.ScriptParser) as IEnumerable<IScriptLine>;
            if (engine == null) throw new InvalidOperationException("Script command reference is unavailable.");
            var hash = viewPort.Model.GetShortGameCode();
            var results = engine
               .OfType<ScriptLine>()
               .Where(line => line.MatchesGame(hash) && line.LineCommand.Contains(query, StringComparison.OrdinalIgnoreCase))
               .GroupBy(line => line.LineCommand, StringComparer.OrdinalIgnoreCase)
               .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
               .Select(group => {
                  var line = group.First();
                  return new CommandResult(line.LineCommand, line.Args.OfType<ScriptArg>().Select(arg =>
                     new CommandArgument(arg.Name, arg.Type.ToString(), arg.EnumTableName)).ToList());
               }).ToList();
            var referencePath = Path.Combine(AppContext.BaseDirectory, "resources", "scriptReference.txt");
            if (File.Exists(referencePath)) {
               foreach (var line in File.ReadLines(referencePath)) {
                  var text = line.TrimStart(' ', '\t', '#');
                  var tokens = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                  if (tokens.Length < 2 || !tokens[0].Equals("XX", StringComparison.OrdinalIgnoreCase) ||
                      !tokens[1].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                  if (results.Any(item => item.Name.Equals(tokens[1], StringComparison.OrdinalIgnoreCase))) continue;
                  var definition = text.Substring(text.IndexOf(tokens[1], StringComparison.Ordinal) + tokens[1].Length);
                  var comment = definition.IndexOf('#');
                  if (comment >= 0) definition = definition.Substring(0, comment);
                  var arguments = definition.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(token => {
                     var name = token.TrimEnd('.', ':', ',');
                     return new CommandArgument(name, "Reference", null);
                  }).ToList();
                  results.Add(new CommandResult(tokens[1], arguments));
               }
            }
            return (IReadOnlyList<CommandResult>)results.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
         });
      }

      public static async Task<ToolResponse<CompileResult>> CompileScript(string romPath, string script, int address = 0) {
         try {
            if (!File.Exists(romPath)) return ToolResponse<CompileResult>.Failure("rom_not_found", $"ROM not found: {romPath}");
            var bytes = await File.ReadAllBytesAsync(romPath);
            var singletons = new Singletons();
            var metadataPath = Path.ChangeExtension(romPath, ".toml");
            var metadata = File.Exists(metadataPath) ? new StoredMetadata(File.ReadAllLines(metadataPath)) : new StoredMetadata(Array.Empty<string>());
            var model = new HardcodeTablesModel(singletons, bytes, metadata);
            var viewPort = new ViewPort(romPath, model, InstantDispatch.Instance, singletons, new MapTutorialsViewModel(), new StubFileSystem());
            await viewPort.InitializationWorkload;
            var parser = viewPort.Tools.CodeTool.ScriptParser;
            var errors = new List<CompileIssue>();
            void Handler(object sender, ScriptErrorInfo info) => errors.Add(new CompileIssue(info.Segment.Line + 1, info.Message));
            parser.CompileError += Handler;
            var token = new ModelDelta();
            var text = script ?? string.Empty;
            byte[] result;
            try {
               result = parser.Compile(token, model, address, ref text, out var _, out var _);
            } finally {
               parser.CompileError -= Handler;
            }
            var compile = new CompileResult(result?.Length ?? 0, errors, Array.Empty<CompileIssue>(), result ?? Array.Empty<byte>());
            if (result == null || errors.Count > 0) return new ToolResponse<CompileResult>(SchemaVersion, false, compile, new ToolError("compile_error", errors.Count == 0 ? "The script could not be compiled." : errors[0].Message, errors.Count == 0 ? null : errors[0].Line));
            return ToolResponse<CompileResult>.Success(compile);
         } catch (ArgumentException ex) {
            return ToolResponse<CompileResult>.Failure("invalid_argument", ex.Message);
         } catch (Exception ex) {
            return ToolResponse<CompileResult>.Failure("tool_error", ex.Message);
         }
      }

      private static FreeValueResult FindFlagInRange(IDataModel model, ISet<int> used, string range) {
         var (start, end, name) = FlagRange(model, range);
         var excluded = new HashSet<int>(used);
         for (var value = 0; value < 0x10000; value++) if (value < start || value > end) excluded.Add(value);
         var candidate = Flags.NextFreeFlag(model, excluded);
         return candidate >= start && candidate <= end ? new FreeValueResult(candidate, name, start, end) : new FreeValueResult(-1, name, start, end);
      }

      private static FreeValueResult FindVariableInRange(IDataModel model, ISet<int> used, string range) {
         var (start, end, name) = VariableRange(range);
         var excluded = new HashSet<int>(used);
         for (var value = 0; value < 0x8000; value++) if (value < start || value > end) excluded.Add(value);
         var candidate = Flags.NextFreeVariable(model, excluded);
         return candidate >= start && candidate <= end ? new FreeValueResult(candidate, name, start, end) : new FreeValueResult(-1, name, start, end);
      }

      private static (int Start, int End, string Name) FlagRange(IDataModel model, string range) {
         var normalized = (range ?? "persistent").Trim().ToLowerInvariant();
         return normalized switch {
            "persistent" or "story" => (0x21, model.IsEmerald() ? 0x4FF : 0x4FF, "persistent-story"),
            "temporary" or "temp" => (0x00, 0x20, "temporary"),
            "trainer" => (0x500, 0x7FF, "trainer"),
            "system" => (0x800, 0xFFFF, "system"),
            "all" => (0x00, 0xFFFF, "all"),
            _ => throw new ArgumentException($"Unknown flag range: {range}.")
         };
      }

      private static (int Start, int End, string Name) VariableRange(string range) {
         var normalized = (range ?? "persistent").Trim().ToLowerInvariant();
         return normalized switch {
            "persistent" or "story" => (0x4034, 0x40FF, "persistent"),
            "all" => (0x4034, 0x7FFF, "all"),
            _ => throw new ArgumentException($"Unknown variable range: {range}.")
         };
      }

      private static bool IsDailyName(string name) => name?.Contains("DAILY", StringComparison.OrdinalIgnoreCase) == true;

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

      private static object NullablePointer(int value) => value == HavenSoft.HexManiac.Core.ViewModels.DataFormats.Pointer.NULL ? null : value;

      public sealed record ToolResponse<T>(int SchemaVersion, bool Ok, T Result, ToolError Error) {
         public static ToolResponse<T> Success(T result) => new(AgentApi.SchemaVersion, true, result, null);
         public static ToolResponse<T> Failure(string code, string message) => new(AgentApi.SchemaVersion, false, default, new ToolError(code, message));
      }
      public sealed record ToolError(string Code, string Message, int? Line = null);
      public sealed record MapResult(int MapId, int Width, int Height, int LayoutId, EventCounts EventCounts);
      public sealed record EventCounts(int Objects, int Warps, int Scripts, int Signposts);
      public sealed record FreeValueResult(int Value, string Range = null, int RangeStart = -1, int RangeEnd = -1);
      public sealed record ScriptResult(int Address, string Text);
      public sealed record CompileIssue(int Line, string Message);
      public sealed record CompileResult(int ByteSize, IReadOnlyList<CompileIssue> Errors, IReadOnlyList<CompileIssue> Warnings, [property: JsonIgnore] byte[] Bytes);
      public sealed record ConstantResult(string Kind, string ListName, IReadOnlyList<ConstantEntry> Values);
      public sealed record ConstantEntry(int Value, string Name, string NameSource);
      public sealed record CommandResult(string Name, IReadOnlyList<CommandArgument> Arguments);
      public sealed record CommandArgument(string Name, string Type, string EnumTable);
   }
}
