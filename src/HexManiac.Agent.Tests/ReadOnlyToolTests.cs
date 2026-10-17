using HavenSoft.HexManiac.Agent;
using HavenSoft.HexManiac.Core.Models;
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
         var singletons = new Singletons();
         var model = new HardcodeTablesModel(singletons, await File.ReadAllBytesAsync(romPath), new StoredMetadata(Array.Empty<string>()));
         Directory.SetCurrentDirectory(AppContext.BaseDirectory);
         var viewPort = new ViewPort(romPath, model, InstantDispatch.Instance, singletons, new MapTutorialsViewModel(), new StubFileSystem());
         await viewPort.InitializationWorkload;
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
   }
}
