using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace HavenSoft.HexManiac.Agent.Tests {
   public class HeadlessWriteTests {
      private static readonly string RomPath = Path.GetFullPath(Path.Combine(
         AppContext.BaseDirectory,
         "..", "..", "..", "..", "..",
         "artifacts",
         "HexManiac.Integration",
         "bin",
         "Debug",
         "net6.0",
         "sampleFiles",
         "Pokemon FireRed.gba"
      ));
      private const string MapName = "maps.bank3.Pallet Town.3-0";

      [SkippableFact]
      public async Task CreateNpc_Undo_RestoresOriginalRomBytes() {
         Skip.IfNot(File.Exists(RomPath));
         var original = File.ReadAllBytes(RomPath);
         Directory.SetCurrentDirectory(AppContext.BaseDirectory);
         var singletons = new Singletons();
         var model = new HardcodeTablesModel(singletons, original.ToArray(), new StoredMetadata(Array.Empty<string>()));
         var viewPort = new ViewPort(
            RomPath,
            model,
            InstantDispatch.Instance,
            singletons,
            new MapTutorialsViewModel(),
            new StubFileSystem()
         );

         await viewPort.InitializationWorkload;
         viewPort.Goto.Execute(MapName);
         var mapEditor = viewPort.MapEditor;
         Assert.NotNull(mapEditor);
         var map = mapEditor.PrimaryMap;
         Assert.NotNull(map);
         var events = map.EventGroup;
         Assert.NotNull(events);
         var countBefore = events.Objects.Count;

         var objectEvent = map.CreateObjectEvent(0, Pointer.NULL);
         Assert.NotNull(objectEvent);
         mapEditor.Templates.CreateNPC(objectEvent, viewPort.ChangeHistory.CurrentChange);
         viewPort.ChangeHistory.ChangeCompleted();

         var changedEvents = map.EventGroup;
         Assert.NotNull(changedEvents);
         Assert.Equal(countBefore + 1, changedEvents.Objects.Count);
         viewPort.Undo.Execute(null);

         Assert.Equal(original, model.ToArray());
      }
   }
}
