<project_philosophy>
Focus: Gemini API cost optimization, LiteDB hot/cold memory hierarchy, and async double-buffered scheduling.
</project_philosophy>

<engineering_rules>
- **API/Cost**: Consolidate LLM prompts (use JSON mode). Skip API calls for physical/transit states.
- **Memory**: Respect LiteDB hot/cold eviction hierarchies. Never load full collections into RAM.
- **Concurrency**: Use `async`/`await` throughout. NEVER use `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`.
- **Formatting**: Strictly follow the target file's style.
</engineering_rules>

<critical_rules>
- **Build/Run**: `dotnet build`, `dotnet run --project MundusVivens.Prototype`
- **Secrets**: NEVER commit `MundusVivens.Prototype/Config/google-credentials.json`
- **Paths**: Use relative paths (`../MundusVivens.GameServer.Cpp/`, `../Obsidian.Agent/`, etc.).
</critical_rules>

<context_triggers>
- **Agent Architecture**: Gemini API prompts, LiteDB memory hierarchy, scheduler -> `../Obsidian.Agent/MundusVivens/docs/02_agent_design.md`
- **Troubleshooting**: Runtime errors, memory eviction bugs, runbook -> `../Obsidian.Agent/troubleshooting/mundus_vivens.md`
</context_triggers>

<post_action>
- **Log**: Document resolved bugs in `../Obsidian.Agent/troubleshooting/mundus_vivens.md`. (Ignore simple refactors/optimizations)
- **Sync**: Update specs in `../Obsidian.Agent/MundusVivens/docs/` if architecture changes.
</post_action>
