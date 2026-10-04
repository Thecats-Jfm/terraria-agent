using System;
using TerrariaAgent.Protocol;

internal static class PlatformUpChecks
{
    private sealed class Fixture
    {
        internal long Now = 10000;
        internal readonly LeaseGate Gate;
        internal Fixture(bool enabled = true)
        {
            Gate = new LeaseGate(() => Now, false, enabled);
            string reason;
            Require(Gate.OpenSession("up-owner", out reason), "session");
            Gate.UpdateContext("up-world", false, false, false); Gate.RecordObservation(1, Now);
            var consent = Gate.Snapshot();
            Require(Gate.PermitNextArm(consent.SessionId, consent.WorldId, consent.ControlEpoch, Now), "human permission");
            var arm = Up(1); arm.Type = "arm"; arm.Up = arm.Right = false;
            Require(Gate.ExplicitArm(arm, out reason), "arm");
        }
        internal AgentRequest Up(long sequence = 2)
        { return new AgentRequest { Type="action", SessionId="up-owner", WorldId="up-world",
            Sequence=sequence, ObservationSequence=1, TtlMs=250, Right=true, Up=true }; }
        internal void Apply()
        { string reason; Require(Gate.TryApplyAction(Up(), Now, out reason), "Up admission:" + reason); }
    }

    internal static void Admission()
    {
        var disabled=new Fixture(false); string reason;
        Require(!disabled.Gate.TryApplyAction(disabled.Up(), disabled.Now, out reason) &&
            reason=="gameplay_actions_disabled", "A accepted Up"); Neutral(disabled.Gate);
        foreach (string conflict in new[] { "standalone", "both", "jump", "use", "craft", "recipe", "aim" })
        {
            var f=new Fixture(); var request=f.Up();
            if(conflict=="standalone") request.Right=false;
            if(conflict=="both") request.Left=true;
            if(conflict=="jump") request.Jump=true;
            if(conflict=="use") {request.UseItem=true;request.SelectedSlot=1;request.AimTileX=request.AimTileY=5;}
            if(conflict=="craft") request.CraftWorkBench=true;
            if(conflict=="recipe") request.CraftRecipe=GameplayRecipeIds.Torch;
            if(conflict=="aim") request.AimTileX=request.AimTileY=5;
            Require(!f.Gate.TryApplyAction(request,f.Now,out reason), "conflicting Up admitted:"+conflict);
            Neutral(f.Gate);
        }
        var valid=new Fixture(); valid.Apply();
        Require(valid.Gate.PollInputs().Up && valid.Gate.PollInputs().Right, "legal Up missing");
    }

    internal static void ProofAndCopy()
    {
        var f=new Fixture(); f.Apply(); var snapshot=f.Gate.Snapshot();
        snapshot.Inputs.Up=false;
        Require(f.Gate.PollInputs().Up, "snapshot aliases stored Up");
        InputState input=f.Gate.PollInputs();
        var proof=new GameplayObservation {CanStepRight=true,StepRequiresUpRight=true};
        Require(PlatformStepGuard.CanApply(input,proof), "same-direction proof refused");
        var copy=proof.Copy(); proof.StepRequiresUpRight=false;
        Require(copy.StepRequiresUpRight && !PlatformStepGuard.CanApply(input,proof), "withdrawn proof reused");
        Require(!PlatformStepGuard.CanApply(input,new GameplayObservation {CanStepLeft=true,StepRequiresUpLeft=true}),
            "opposite direction proof accepted");
        Require(!PlatformStepGuard.CanApply(input,new GameplayObservation {StepRequiresUpRight=true}),
            "need-Up without ground proof accepted");
        Require(!PlatformStepGuard.CanApply(input,null), "unknown proof accepted");
        var decoded=JsonCodec.Deserialize<InputState>(JsonCodec.Serialize(input));
        Require(decoded.Up && decoded.Right && !JsonCodec.Deserialize<InputState>(System.Text.Encoding.UTF8.GetBytes("{}")).Up,
            "Up wire copy or omitted default changed");
    }

    internal static void Release()
    {
        foreach(string boundary in new[] {"expiry","disconnect","manual","stop","emergency","death","menu","text","world"})
        {
            var f=new Fixture(); f.Apply();
            if(boundary=="expiry") f.Now+=250;
            if(boundary=="disconnect") f.Gate.Disconnect("up-owner");
            if(boundary=="manual") f.Gate.ManualTakeover();
            if(boundary=="stop") f.Gate.Stop("up-owner");
            if(boundary=="emergency") f.Gate.EmergencyStop();
            if(boundary=="death") f.Gate.UpdateContext("up-world",true,false,false);
            if(boundary=="menu") f.Gate.UpdateContext(null,false,true,false);
            if(boundary=="text") f.Gate.UpdateContext("up-world",false,false,true);
            if(boundary=="world") f.Gate.UpdateContext("new-up-world",false,false,false);
            Neutral(f.Gate);
            string reason;
            Require(!f.Gate.TryApplyAction(f.Up(3),f.Now,out reason), "renewal rearms Up after:"+boundary);
            Neutral(f.Gate);
        }
        var reconnect=new Fixture(); reconnect.Apply(); reconnect.Gate.Disconnect("up-owner");
        string ignored; Require(reconnect.Gate.OpenSession("replacement-up-owner",out ignored), "reconnect");
        Require(!reconnect.Gate.Snapshot().ArmPermitted && !reconnect.Gate.Snapshot().CanOperatorArm,
            "reconnect recreates Up permission"); Neutral(reconnect.Gate);
    }

    internal static void AscentHandoff()
    {
        var f = new Fixture(); f.Apply(); var requested = f.Gate.PollInputs();
        var before = f.Gate.Snapshot(); InputState applied;
        Require(PlatformStepGuard.TryResolve(requested,
            new GameplayObservation { CanStepRight = true, StepRequiresUpRight = true }, out applied) &&
            applied.Up && applied.Right, "current ascent proof did not retain normal Up");
        Require(PlatformStepGuard.TryResolve(requested,
            new GameplayObservation { CanStepRight = true }, out applied) &&
            applied.Right && !applied.Left && !applied.Up && !applied.Jump && !applied.UseItem,
            "completed ascent did not release only Up");
        Require(requested.Up && f.Gate.PollInputs().Up, "handoff changed stored action instead of its copy");
        var after = f.Gate.Snapshot();
        Require(after.State == before.State && after.ControlEpoch == before.ControlEpoch &&
            after.LastSequence == before.LastSequence && after.ExpiresAtMs == before.ExpiresAtMs,
            "handoff renewed or rearmed the lease");
        foreach (var proof in new[] { null, new GameplayObservation(),
            new GameplayObservation { CanStepLeft = true },
            new GameplayObservation { StepRequiresUpRight = true } })
        {
            Require(!PlatformStepGuard.TryResolve(requested, proof, out applied) &&
                !applied.Left && !applied.Right && !applied.Up, "unknown or opposite ground continued movement");
        }
        var left = requested.Copy(); left.Right = false; left.Left = true;
        Require(PlatformStepGuard.TryResolve(left, new GameplayObservation { CanStepLeft = true }, out applied) &&
            applied.Left && !applied.Right && !applied.Up, "left ascent handoff selected wrong direction");
        foreach (string conflict in new[] { "standalone", "both", "jump", "use", "craft", "recipe", "aim" })
        {
            var bad = requested.Copy();
            if (conflict == "standalone") bad.Right = false;
            if (conflict == "both") bad.Left = true;
            if (conflict == "jump") bad.Jump = true;
            if (conflict == "use") bad.UseItem = true;
            if (conflict == "craft") bad.CraftWorkBench = true;
            if (conflict == "recipe") bad.CraftRecipe = GameplayRecipeIds.Torch;
            if (conflict == "aim") bad.AimTileX = 3;
            Require(!PlatformStepGuard.TryResolve(bad, new GameplayObservation { CanStepRight = true }, out applied) &&
                !applied.Left && !applied.Right && !applied.Up, "incompatible handoff admitted:" + conflict);
        }
    }

    internal static void Transaction()
    {
        var f=new Fixture(); f.Apply(); int writes=0; string reason;
        f.Gate.Stop("up-owner");
        Require(!f.Gate.TryExecuteCurrent("up-owner","up-world",2,()=>++writes,out reason) && writes==0,
            "revoked Up transaction wrote input");
        var stale=new Fixture(); stale.Now+=1001; stale.Gate.RecordObservation(2,stale.Now);
        Require(!stale.Gate.TryApplyAction(stale.Up(),stale.Now,out reason), "stale observation restored Up");
        Neutral(stale.Gate);
    }
    private static void Neutral(LeaseGate gate)
    { foreach(var input in new[] {gate.PollInputs(),gate.Snapshot().Inputs})
        Require(!input.Up && !input.Left && !input.Right && !input.Jump && !input.UseItem &&
            !input.CraftWorkBench && input.CraftRecipe==null,"continuous input survived revocation"); }
    private static void Require(bool condition,string reason)
    {if(!condition)throw new InvalidOperationException("platform_up_boundary:"+reason);}
}
