namespace LMP;

// TODO(L.3): Microsoft.Extensions.AI.Agents is not available in MEAI 10.4.1.
// When the Agents package is released, implement ToTrajectory() to convert an AgentThread
// to a Trajectory for use with LMP trajectory-aware optimizers (GEPA, SIMBA).
//
// Expected mapping:
//   User messages          → Turn(TurnKind.UserToAgent, ...)
//   Assistant messages     → Turn(TurnKind.AgentToUser, ...)
//   Tool call messages     → Turn(TurnKind.ToolCall, ...)
//   Tool result messages   → Turn(TurnKind.ToolResult, ...)
//   Agent-to-agent         → Turn(TurnKind.AgentToAgent, ...)
//
// Example stub:
//   public static Trajectory ToTrajectory(this AgentThread thread) { ... }
//
// Add a PackageReference to Microsoft.Extensions.AI.Agents in LMP.Core.csproj and
// update Directory.Packages.props with the version when the package is available.
