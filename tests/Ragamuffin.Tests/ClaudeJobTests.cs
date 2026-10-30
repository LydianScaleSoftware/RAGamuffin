using Ragamuffin.Claude;

namespace Ragamuffin.Tests;

public class ClaudeJobTests
{
    static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Problem_prompt_names_the_document_by_id_and_asks_for_a_report()
    {
        var prompt = ClaudeTasks.Prompt(ClaudeTask.Problems, Id, "Beneath Apple DOS.pdf", isPdf: true, "42, 77", null);
        Assert.Contains($"\"Beneath Apple DOS.pdf\" (id {Id})", prompt);
        Assert.Contains("which=\"problems\"", prompt);
        Assert.Contains("only those among pages 42, 77", prompt);
        Assert.Contains("rag-check-pdf", prompt);
        Assert.Contains("short report", prompt);
    }

    [Fact]
    public void Sample_prompt_verifies_only_when_the_sample_matched()
    {
        var prompt = ClaudeTasks.Prompt(ClaudeTask.Sample, Id, "x.pdf", true, null, null);
        Assert.Contains("which=\"sample\"", prompt);
        Assert.Contains("verify_document", prompt);
        Assert.Contains("don't verify it", prompt);
    }

    [Fact]
    public void Custom_prompt_carries_the_users_instructions_without_the_page_checking_guide()
    {
        var prompt = ClaudeTasks.Prompt(ClaudeTask.Custom, Id, "notes.md", false, null, "  Re-chunk the listing by routine.  ");
        Assert.Contains("The user's instructions for this run:\nRe-chunk the listing by routine.", prompt.Replace("\r\n", "\n"));
        Assert.DoesNotContain("rag-check-pdf", prompt);
    }

    [Theory]
    [InlineData("12", true)]
    [InlineData("3-7", true)]
    [InlineData("12, 40–45, 50", true)]
    [InlineData("", false)]
    [InlineData("12,", false)]
    [InlineData("all", false)]
    public void Page_lists_are_validated(string pages, bool valid) => Assert.Equal(valid, ClaudeTasks.ValidPages(pages));

    [Fact]
    public void Arguments_limit_claude_to_the_library_tools()
    {
        var args = ClaudeRunner.Arguments("do it", new RagOptions().Claude);
        Assert.Equal(["-p", "do it"], args.Take(2));
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("Skill", args[args.IndexOf("--tools") + 1]);
        var allowed = args[args.IndexOf("--allowedTools") + 1].Split(',');
        Assert.Contains("mcp__rag__set_page_text", allowed);
        Assert.DoesNotContain("mcp__rag__delete_document", allowed);
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void Stream_lines_become_activity_and_result()
    {
        var job = new ClaudeJob();
        ClaudeRunner.Apply(job, """{"type":"system","subtype":"init","session_id":"s1","model":"claude-sonnet"}""");
        ClaudeRunner.Apply(job, """{"type":"assistant","message":{"content":[{"type":"text","text":"Checking page 42."},{"type":"tool_use","name":"mcp__rag__get_page","input":{"document":"x","page":42}}]}}""");
        ClaudeRunner.Apply(job, """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__rag__set_page_text","input":{"document":"x","pages":[{"page":42,"text":"a"},{"page":77,"text":"b"}]}}]}}""");
        ClaudeRunner.Apply(job, """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":[{"type":"text","text":"boom"}]}]}}""");
        ClaudeRunner.Apply(job, "not json");
        ClaudeRunner.Apply(job, """{"type":"result","subtype":"success","is_error":false,"result":"Fixed 2 pages.","total_cost_usd":0.42}""");

        Assert.Equal("s1", job.SessionId);
        Assert.Equal("claude-sonnet", job.Model);
        Assert.Equal(2, job.ToolCalls);
        Assert.Equal(["Checking page 42.", "→ get_page p.42", "→ set_page_text pages 42, 77", "  tool error: boom"], job.Activity);
        Assert.Equal("Fixed 2 pages.", job.Result);
        Assert.Equal(0.42m, job.CostUsd);
        Assert.Null(job.Error);
    }

    [Fact]
    public void A_login_failure_says_how_to_log_in()
    {
        var job = new ClaudeJob();
        ClaudeRunner.Apply(job, """{"type":"result","subtype":"success","is_error":true,"result":"Failed to authenticate: OAuth session expired"}""");
        Assert.Contains("/login", job.Error);
    }
}
