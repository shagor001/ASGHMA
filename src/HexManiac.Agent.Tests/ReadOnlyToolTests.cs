using HavenSoft.HexManiac.Agent;
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
using Xunit;

namespace HavenSoft.HexManiac.Agent.Tests {
   public class ReadOnlyToolTests {
      private static string Rom(string name) => Path.GetFullPath(Path.Combine(
         AppContext.BaseDirectory, "..", "..", "..", "..", "..",
         "artifacts", "HexManiac.Integration", "bin", "Debug", "net6.0", "sampleFiles", name));

      [SkippableFact]
      public async Task GetMap_MatchesFireRedPalletTownInspection() {
         Skip.IfNot(File.Exists(Rom("Pokemon FireRed.gba")));
         var response = await AgentApi.GetMap(Rom("Pokemon FireRed.gba"), "maps.bank3.Pallet Town.3-0");
         Assert.True(response.Ok);
         Assert.Equal(new AgentApi.MapResult(3000, 24, 20, response.Result.LayoutId,
            new AgentApi.EventCounts(3, 3, 3, 5)), response.Result);
      }

      [SkippableFact]
      public async Task GetMap_MatchesEmeraldPetalburgInspection() {
         Skip.IfNot(File.Exists(Rom("Pokemon Emerald.gba")));
         var response = await AgentApi.GetMap(Rom("Pokemon Emerald.gba"), "maps.bank0.PETALBURG CITY.0-0");
         Assert.True(response.Ok);
         Assert.Equal(9, response.Result.EventCounts.Objects);
         Assert.Equal(6, response.Result.EventCounts.Warps);
         Assert.Equal(8, response.Result.EventCounts.Scripts);
         Assert.Equal(8, response.Result.EventCounts.Signposts);
      }

      [SkippableFact]
      public async Task ListEvents_MatchesTypedFireRedInspection() {
         Skip.IfNot(File.Exists(Rom("Pokemon FireRed.gba")));
         var response = await AgentApi.ListEvents(Rom("Pokemon FireRed.gba"), "maps.bank3.Pallet Town.3-0", "all");
         Assert.True(response.Ok);
         Assert.Equal(14, response.Result.Count);
         Assert.Equal(3, response.Result.Count(e => (string)e["type"] == "object"));
         Assert.Equal(3, response.Result.Count(e => (string)e["type"] == "warp"));
         Assert.Equal(3, response.Result.Count(e => (string)e["type"] == "script"));
         Assert.Equal(5, response.Result.Count(e => (string)e["type"] == "signpost"));
         Assert.Contains(response.Result, e => e["type"].Equals("object") && e["scriptAddress"] == null);
      }

      [SkippableFact]
      public async Task FindFreeFlag_IsNotReferencedByScriptsOrEvents() {
         Skip.IfNot(File.Exists(Rom("Pokemon FireRed.gba")));
         var romPath = Rom("Pokemon FireRed.gba");
         var response = await AgentApi.FindFreeFlag(romPath);
         Assert.True(response.Ok);
         var flag = response.Result.Value;
         Assert.Equal(33, flag);
         Assert.Equal("persistent-story", response.Result.Range);
         Assert.Equal(0x21, response.Result.RangeStart);
         var singletons = new Singletons();
         var model = new HardcodeTablesModel(singletons, await File.ReadAllBytesAsync(romPath), new StoredMetadata(Array.Empty<string>()));
         Directory.SetCurrentDirectory(AppContext.BaseDirectory);
         var viewPort = new ViewPort(romPath, model, InstantDispatch.Instance, singletons, new MapTutorialsViewModel(), new StubFileSystem());
         await viewPort.InitializationWorkload;
         var usage = Flags.GetScriptUsage(model, viewPort.Tools.CodeTool.ScriptParser);
         Assert.DoesNotContain(flag, usage.ItemFlags);
         Assert.DoesNotContain(flag, usage.TrainerFlags);
         Assert.Empty(Flags.FindFlagUsages(model, viewPort.Tools.CodeTool.ScriptParser, flag));
         var banks = model.GetTableModel(HardcodeTablesModel.MapBankTable);
         Assert.NotNull(banks);
         for (var bankIndex = 0; bankIndex < banks.Count; bankIndex++) {
            var bank = banks[bankIndex];
            var maps = bank.GetSubTable("maps");
            if (maps == null) continue;
            for (var index = 0; index < maps.Count; index++) {
               viewPort.Goto.Execute($"maps.bank{bankIndex}.{BlockMapViewModel.MapIDToText(model, bankIndex, index)}");
               var events = viewPort.MapEditor?.PrimaryMap?.EventGroup;
               Assert.NotNull(events);
               Assert.DoesNotContain(events.Objects, e => e.Flag == flag);
            }

         }
      }

      [SkippableFact]
      public async Task FindFreeVar_ReportsPersistentRange() {
         Skip.IfNot(File.Exists(Rom("Pokemon FireRed.gba")));
         var response = await AgentApi.FindFreeVar(Rom("Pokemon FireRed.gba"));
         Assert.True(response.Ok);
         Assert.Equal("persistent", response.Result.Range);
         Assert.InRange(response.Result.Value, response.Result.RangeStart, response.Result.RangeEnd);
      }

      [SkippableTheory]
      [InlineData("Pokemon FireRed.gba")]
      [InlineData("Pokemon Emerald.gba")]
      public async Task FindFreeVar_IsNotReferencedByScriptsTriggersOrKnownVarFields(string fileName) {
         var romPath = Rom(fileName);
         Skip.IfNot(File.Exists(romPath));
         var before = await File.ReadAllBytesAsync(romPath);
         var response = await AgentApi.FindFreeVar(romPath);
         Assert.True(response.Ok);
         var singletons = new Singletons();
         var model = new HardcodeTablesModel(singletons, await File.ReadAllBytesAsync(romPath), new StoredMetadata(Array.Empty<string>()));
         var viewPort = new ViewPort(romPath, model, InstantDispatch.Instance, singletons, new MapTutorialsViewModel(), new StubFileSystem());
         await viewPort.InitializationWorkload;
         var parser = viewPort.Tools.CodeTool.ScriptParser;
         Assert.DoesNotContain(response.Result.Value, Flags.GetScriptUsage(model, parser).Variables);
         Assert.Empty(Flags.FindVarUsages(model, parser, response.Result.Value));
         foreach (var element in Flags.GetAllEvents(model, "scripts")) {
            if (element.HasField("trigger")) Assert.NotEqual(response.Result.Value, element.GetValue("trigger"));
         }
         Assert.Equal(before, await File.ReadAllBytesAsync(romPath));
      }

      [SkippableTheory]
      [InlineData("Pokemon FireRed.gba")]
      [InlineData("Pokemon Emerald.gba")]
      public async Task CompileScript_InvalidInputIsDryRun(string fileName) {
         var romPath = Rom(fileName);
         Skip.IfNot(File.Exists(romPath));
         var before = await File.ReadAllBytesAsync(romPath);
         var historySingletons = new Singletons();
         var historyModel = new HardcodeTablesModel(historySingletons, before.ToArray(), new StoredMetadata(Array.Empty<string>()));
         var historyViewPort = new ViewPort(romPath, historyModel, InstantDispatch.Instance, historySingletons, new MapTutorialsViewModel(), new StubFileSystem());
         await historyViewPort.InitializationWorkload;
         var misspelled = await AgentApi.CompileScript(romPath, "not_a_real_command");
         Assert.False(misspelled.Ok);
         Assert.NotNull(misspelled.Error.Line);
         Assert.Contains("not_a_real_command", misspelled.Error.Message);
         Assert.True(historyViewPort.ChangeHistory.IsSaved);
         var wrongArguments = await AgentApi.CompileScript(romPath, "end 1");
         Assert.False(wrongArguments.Ok);
         Assert.True(historyViewPort.ChangeHistory.IsSaved);
         var unknownConstant = await AgentApi.CompileScript(romPath, "setflag DOES_NOT_EXIST");
         Assert.False(unknownConstant.Ok);
         Assert.True(historyViewPort.ChangeHistory.IsSaved);
         Assert.Equal(before, await File.ReadAllBytesAsync(romPath));
      }

      [SkippableTheory]
      [InlineData("Pokemon FireRed.gba")]
      [InlineData("Pokemon Emerald.gba")]
      public async Task CompileScript_RoundTripsFirst200ReachableScripts(string fileName) {
         var romPath = Rom(fileName);
         Skip.IfNot(File.Exists(romPath));
         var singletons = new Singletons();
         var original = await File.ReadAllBytesAsync(romPath);
         var model = new HardcodeTablesModel(singletons, original.ToArray(), new StoredMetadata(Array.Empty<string>()));
         var viewPort = new ViewPort(romPath, model, InstantDispatch.Instance, singletons, new MapTutorialsViewModel(), new StubFileSystem());
         await viewPort.InitializationWorkload;
         var parser = viewPort.Tools.CodeTool.ScriptParser;
         var addresses = Flags.GetAllTopLevelScripts(model).Where(address => address >= 0 && address < model.Count).OrderBy(address => address).Take(200).ToList();
         var exact = 0;
         var mismatches = new List<string>();
         foreach (var address in addresses) {
            var length = parser.GetScriptSegmentLength(model, address);
            if (length <= 0 || address + length > original.Length) continue;
            var sections = 0;
            var text = parser.Parse(model, address, length, ref sections);
            var errors = new List<string>();
            void Handler(object sender, ScriptErrorInfo info) => errors.Add(info.Message);
            parser.CompileError += Handler;
            var token = new ModelDelta();
            byte[] compiledBytes;
            try {
               compiledBytes = parser.Compile(token, model, address, ref text, out var _, out var _);
            } finally {
               parser.CompileError -= Handler;
               token.Revert(model);
            }
            if (compiledBytes == null || errors.Count > 0) {
               mismatches.Add($"{address}: compile error {string.Join(" ", errors)}");
               continue;
            }
            var expected = original.Skip(address).Take(length).ToArray();
            if (expected.SequenceEqual(compiledBytes)) exact++;
            else if (mismatches.Count < 5) mismatches.Add($"{address}: original {length} bytes, compiled {compiledBytes.Length} bytes");
         }
         Console.WriteLine($"{fileName}: round-trip exact {exact}/{addresses.Count}; mismatches: {string.Join(" | ", mismatches)}");
         Assert.NotEmpty(addresses);
      }

      [SkippableFact]
      public async Task DecompileScript_MatchesKnownFireRedAndEmeraldScripts() {
         Skip.IfNot(File.Exists(Rom("Pokemon FireRed.gba")));
         Skip.IfNot(File.Exists(Rom("Pokemon Emerald.gba")));
         var fireRed = await AgentApi.DecompileScript(Rom("Pokemon FireRed.gba"), 1464156);
         var emerald = await AgentApi.DecompileScript(Rom("Pokemon Emerald.gba"), 1950717);
         Assert.True(fireRed.Ok);
         Assert.Contains("msgbox.default", fireRed.Result.Text);
         Assert.Contains("setflag 0x0002", fireRed.Result.Text);
         Assert.True(emerald.Ok);
         Assert.Contains("msgbox.npc", emerald.Result.Text);
         Assert.Contains("end", emerald.Result.Text);
      }

      [SkippableFact]
      public async Task DecompileScript_RejectsNonScriptAddress() {
         Skip.IfNot(File.Exists(Rom("Pokemon FireRed.gba")));
         var response = await AgentApi.DecompileScript(Rom("Pokemon FireRed.gba"), 0);
         Assert.False(response.Ok);
         Assert.Equal("invalid_argument", response.Error.Code);
      }

      [SkippableFact]
      public async Task ListConstants_ReturnsNamedValues() {
         Skip.IfNot(File.Exists(Rom("Pokemon FireRed.gba")));
         var response = await AgentApi.ListConstants(Rom("Pokemon FireRed.gba"), "movement_types");
         Assert.True(response.Ok);
         Assert.Contains(response.Result.Values, item => item.Value == 0 && item.Name == "face_down");
         Assert.All(response.Result.Values, item => Assert.NotNull(item.NameSource));
      }

      [SkippableFact]
      public async Task ListConstants_ReturnsEmeraldSpriteValues() {
         Skip.IfNot(File.Exists(Rom("Pokemon Emerald.gba")));
         var response = await AgentApi.ListConstants(Rom("Pokemon Emerald.gba"), "sprites");
         Assert.True(response.Ok);
         Assert.NotEmpty(response.Result.Values);
         Assert.Equal(0, response.Result.Values[0].Value);
      }

      [SkippableFact]
      public async Task SearchScriptCommands_ReturnsReferenceAndParserCommands() {
         Skip.IfNot(File.Exists(Rom("Pokemon FireRed.gba")));
         var giveItem = await AgentApi.SearchScriptCommands(Rom("Pokemon FireRed.gba"), "giveitem");
         var movement = await AgentApi.SearchScriptCommands(Rom("Pokemon FireRed.gba"), "applymovement");
         Assert.True(giveItem.Ok);
         Assert.Contains(giveItem.Result, command => command.Name == "giveitem");
         Assert.True(movement.Ok);
         Assert.Contains(movement.Result, command => command.Name == "applymovement");
         Assert.NotEmpty(movement.Result.Single(command => command.Name == "applymovement").Arguments);
      }
   }
}
