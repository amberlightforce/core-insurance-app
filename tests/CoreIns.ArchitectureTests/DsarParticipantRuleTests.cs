using Mono.Cecil;

namespace CoreIns.ArchitectureTests;

/// <summary>SL6-DSAR-SPI: IDsarParticipant implementations live only in module implementation projects (D-SL6-07), plus the one restrict-only PLT participant in Platform.</summary>
public sealed class DsarParticipantRuleTests
{
    [Fact]
    public void Participants_exist_only_in_module_implementation_projects()
    {
        var allowed = SolutionModel.ModuleNames.Select(SolutionModel.Implementation).ToHashSet();
        var others = SolutionModel.CoreProjects.Where(p => !allowed.Contains(p)).Append("CoreIns.Host");
        var offenders = new List<string>();
        foreach (var project in others)
        {
            var path = SolutionModel.AssemblyPath(project);
            if (!File.Exists(path))
            {
                continue;
            }

            using var asm = ModuleDefinition.ReadModule(path);
            offenders.AddRange(asm.Types
                .Where(t => t.FullName != "CoreIns.Platform.Dsar.PlatformDsarParticipant" && t.Interfaces.Any(i => i.InterfaceType.FullName == "CoreIns.SharedKernel.Dsar.IDsarParticipant"))
                .Select(t => $"{project}: {t.FullName}"));
        }

        offenders.ShouldBeEmpty("IDsarParticipant may be implemented only in module projects");
    }
}
