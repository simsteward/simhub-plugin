# Skill: Using the DeepSeek Reasoner Sub-Agent

This skill governs the use of the `deepseek-reasoner` sub-agent for offloading complex reasoning tasks.

## Capabilities

-   Analyzes complex problems and proposes solutions.
-   Reads from ContextStream to inform its reasoning.
-   Writes lessons, decisions, and new tasks back to ContextStream.
-   Operates exclusively on the `deepseek-r1` model via Ollama.

## Invocation Workflow

1.  **Identify Need**: Determine if a task requires deep reasoning (see `DeepSeekReasonerDelegation.mdc`).
2.  **Gather Context**: Collect relevant information from ContextStream (`lessons`, `decisions`, `tasks`).
3.  **Construct Prompt**: Create a detailed prompt for the sub-agent. The prompt must clearly state the goal, provide all necessary context, and specify the expected output format.
4.  **Execute Task**: Call the `Task` tool with `subagent_type: 'deepseek-reasoner'`.
5.  **Integrate Results**: Process the sub-agent's output, update the main plan, and continue execution.

### Token Usage Logging

To enable token usage logging for the DeepSeek Reasoner, you must call the `ollama_generate` tool through the `log_ollama_usage` wrapper function. This function is defined in the `log-ollama-usage` skill.

Example:

```
const { log_ollama_usage } = require('../log-ollama-usage/log-ollama-usage');

const response = await log_ollama_usage({
  model: 'deepseek-r1:8b',
  prompt: 'Hello, world!',
});
```

### Example Invocation

Here is an example of how to call the sub-agent.

```python
Task(
    subagent_type='deepseek-reasoner',
    description='Analyze API authentication flow',
    prompt='''
    Analyze the existing API authentication flow and propose a refactoring plan to introduce role-based access control.

    **Current Context from ContextStream:**
    - **Relevant Lessons:** "Always validate JWT tokens on the server-side."
    - **Existing Decisions:** "Chose JWT over session cookies for stateless auth."
    - **Active Tasks:** "Implement user profile endpoint."

    Your output should be a markdown-formatted plan with concrete steps. You have write access to ContextStream to capture any new decisions or create sub-tasks.
    '''
)
```
