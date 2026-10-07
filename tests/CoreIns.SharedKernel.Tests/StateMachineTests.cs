using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;
using FsCheck.Xunit;

namespace CoreIns.SharedKernel.Tests;

public sealed class StateMachineTests
{
    public enum Door
    {
        Closed,
        Open,
        Locked,
        Broken,
    }

    public enum Act
    {
        OpenIt,
        CloseIt,
        LockIt,
        UnlockIt,
        Smash,
    }

    private static readonly StateMachine<Door, Act> Machine = StateMachine.Define<Door, Act>(ModuleCode.PLT, "Door")
        .Initial(Door.Closed)
        .Permit(Door.Closed, Act.OpenIt, Door.Open)
        .Permit(Door.Open, Act.CloseIt, Door.Closed)
        .Permit(Door.Closed, Act.LockIt, Door.Locked)
        .Permit(Door.Locked, Act.UnlockIt, Door.Closed)
        .Permit([Door.Closed, Door.Open, Door.Locked], Act.Smash, Door.Broken)
        .Terminal(Door.Broken)
        .Build();

    [Property]
    public bool Only_declared_transitions_fire(Door from, Act trigger)
    {
        var declared = Machine.Transitions.SingleOrDefault(t => t.From == from && t.Trigger == trigger);
        var result = Machine.Fire(from, trigger);
        return declared is null
            ? result.IsFailure && result.Error!.Code.Value == "PLT-ERR-INVALID-STATE-TRANSITION" && !Machine.CanFire(from, trigger)
            : result.IsSuccess && result.Value == declared.To && Machine.CanFire(from, trigger);
    }

    [Fact]
    public void Rejections_are_typed_and_explain_themselves()
    {
        var result = Machine.Fire(Door.Locked, Act.OpenIt);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Metadata["from"].ShouldBe("Locked");
        result.Error.Metadata["trigger"].ShouldBe("OpenIt");
        Should.Throw<InvalidStateTransitionException>(() => Machine.FireOrThrow(Door.Broken, Act.CloseIt));
        Machine.Start(Door.Open).IsFailure.ShouldBeTrue();
        Machine.Start(Door.Closed).Value.ShouldBe(Door.Closed);
    }

    [Fact]
    public void The_table_is_exposed_for_tests_and_check_constraints()
    {
        Machine.StateNames.ShouldBe(["Closed", "Open", "Locked", "Broken"]);
        Machine.CheckConstraintSql("status").ShouldBe("status IN ('Closed', 'Open', 'Locked', 'Broken')");
        Machine.PermittedTriggers(Door.Closed).ShouldBe([Act.OpenIt, Act.LockIt, Act.Smash]);
        Machine.IsTerminal(Door.Broken).ShouldBeTrue();
        Machine.StatePairs.ShouldContain(("Closed", "Broken"));
        Machine.ToMermaid().ShouldContain("Closed --> Open : OpenIt");
        Should.Throw<ArgumentException>(() => Machine.CheckConstraintSql("status; DROP TABLE x"));
    }

    [Fact]
    public void Inconsistent_tables_are_refused()
    {
        Should.Throw<InvalidOperationException>(() => StateMachine.Define<Door, Act>(ModuleCode.PLT, "Duplicate")
            .Initial(Door.Closed)
            .Permit(Door.Closed, Act.OpenIt, Door.Open)
            .Permit(Door.Closed, Act.OpenIt, Door.Locked)
            .Permit([Door.Open, Door.Locked], Act.Smash, Door.Broken)
            .Terminal(Door.Broken)
            .Build()).Message.ShouldContain("declared 2 times");

        Should.Throw<InvalidOperationException>(() => StateMachine.Define<Door, Act>(ModuleCode.PLT, "Unreachable")
            .Initial(Door.Closed)
            .Permit(Door.Closed, Act.OpenIt, Door.Open)
            .Permit(Door.Open, Act.CloseIt, Door.Closed)
            .Permit(Door.Locked, Act.Smash, Door.Broken)
            .Terminal(Door.Broken)
            .Build()).Message.ShouldContain("unreachable");

        Should.Throw<InvalidOperationException>(() => StateMachine.Define<Door, Act>(ModuleCode.PLT, "DeadEnd")
            .Initial(Door.Closed)
            .Permit(Door.Closed, Act.OpenIt, Door.Open)
            .Permit(Door.Closed, Act.LockIt, Door.Locked)
            .Permit(Door.Closed, Act.Smash, Door.Broken)
            .Terminal(Door.Broken)
            .Build()).Message.ShouldContain("no way out");

        Should.Throw<InvalidOperationException>(() => StateMachine.Define<Door, Act>(ModuleCode.PLT, "TerminalWithExit")
            .Initial(Door.Closed)
            .Permit(Door.Closed, Act.OpenIt, Door.Open)
            .Permit(Door.Open, Act.CloseIt, Door.Closed)
            .Permit(Door.Closed, Act.LockIt, Door.Locked)
            .Permit(Door.Locked, Act.Smash, Door.Broken)
            .Terminal(Door.Broken, Door.Locked)
            .Build()).Message.ShouldContain("terminal state Locked has outgoing");

        Should.Throw<InvalidOperationException>(() => StateMachine.Define<Door, Act>(ModuleCode.PLT, "Unused")
            .Initial(Door.Closed)
            .Permit(Door.Closed, Act.OpenIt, Door.Open)
            .Permit(Door.Open, Act.Smash, Door.Broken)
            .Terminal(Door.Broken)
            .Build()).Message.ShouldContain("state Locked is never used");
    }

    [Fact]
    public void States_can_be_declared_not_used_by_a_variant()
    {
        var machine = StateMachine.Define<Door, Act>(ModuleCode.PLT, "NoLock")
            .Initial(Door.Closed)
            .Permit(Door.Closed, Act.OpenIt, Door.Open)
            .Permit(Door.Open, Act.Smash, Door.Broken)
            .Permit(Door.Open, Act.CloseIt, Door.Closed)
            .Terminal(Door.Broken)
            .NotUsed(Door.Locked)
            .Build();

        machine.States.ShouldNotContain(Door.Locked);
        machine.CheckConstraintSql("door").ShouldNotContain("Locked");
    }
}
