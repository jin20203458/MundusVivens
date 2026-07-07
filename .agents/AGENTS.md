# C# AI Server Rules
 
<assigned_role>
For this workspace, you adopt the role of a Senior .NET developer specialized in AI Orchestration.
</assigned_role>

<project_philosophy>
Focus: Gemini API cost optimization, LiteDB Hot/Cold memory hierarchy, async double-buffered scheduling.
</project_philosophy>

<engineering_rules>
- **API/Cost**: Consolidate LLM prompts (use JSON mode). Skip API calls for physical/transit states.
- **Memory**: Respect LiteDB hot/cold eviction hierarchies. Do not load full collections into RAM.
- **Concurrency**: Use `async`/`await` throughout. NEVER use `.Result` or `.Wait()`.
- **Formatting**: Strictly follow the target file's style.
</engineering_rules>

<critical_rules>
- **Build/Run**: `dotnet build`, `dotnet run --project MundusVivens.Prototype`
- **Secrets**: NEVER commit `MundusVivens.Prototype/Config/google-credentials.json`
- **Paths**: Use relative paths (`../MundusVivens.GameServer.Cpp/`, etc.)
</critical_rules>

<context_triggers>
- **Knowledge Base**: If modifying LLM/memory logic, read `docs/02_agent_design.md`.
- **Troubleshooting**: If debugging, read `../Obsidian.Agent/troubleshooting/mundus_vivens.md` before coding.
</context_triggers>

<post_action>
- **Log**: Document resolved bugs in `../Obsidian.Agent/troubleshooting/mundus_vivens.md`. (Ignore simple refactors/optimizations)
- **Sync**: Update specs in `../MundusVivens/docs/` if architecture changes.
</post_action>
