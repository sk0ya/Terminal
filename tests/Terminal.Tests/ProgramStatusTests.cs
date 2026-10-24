using System.Text;
using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>OSC 7501（Program Status Protocol）。仕様の MUST（置き換え・clear・app の継承・上限・制御文字・寿命）を守ること。</summary>
public class ProgramStatusTests
{
    private const string Esc = "\u001b";

    private static string Report(string body) => $"{Esc}]7501;{body}{Esc}\\";

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Blocked_permission_report_becomes_a_record()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        var raised = 0;
        buffer.ProgramStatusChanged += (_, _) => raised++;

        buffer.Process(Report($"state=blocked:kind=permission:app=claude-code:msg={B64("Bash を実行してよいですか")}"));

        var record = Assert.Single(buffer.ProgramStatus);
        Assert.Equal("", record.Id);
        Assert.Equal(ProgramStatusState.Blocked, record.State);
        Assert.Equal(ProgramStatusKind.Permission, record.Kind);
        Assert.Equal("claude-code", record.App);
        Assert.Equal("Bash を実行してよいですか", record.Message);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_report_replaces_its_record_completely_and_bel_terminates_too()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        buffer.Process(Report("state=working:progress=40:app=cargo"));
        buffer.Process($"{Esc}]7501;state=idle\u0007");

        var record = Assert.Single(buffer.ProgramStatus);
        Assert.Equal(ProgramStatusState.Idle, record.State);
        Assert.Null(record.Progress);
        Assert.Null(record.App);   // 置き換えなので前の app も残らない
    }

    [Fact]
    public void Children_inherit_app_and_clear_removes_the_subtree()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        buffer.Process(Report("state=working:id=deploy:app=rex"));
        buffer.Process(Report("state=working:id=deploy/us-east:progress=40"));
        buffer.Process(Report("state=blocked:id=deploy/eu-west:kind=question"));
        buffer.Process(Report("state=done:id=other"));

        Assert.Equal("rex", buffer.ProgramStatus.Single(r => r.Id == "deploy/us-east").App);
        Assert.Equal(40, buffer.ProgramStatus.Single(r => r.Id == "deploy/us-east").Progress);

        buffer.Process(Report("state=clear:id=deploy"));
        Assert.Equal(["other"], buffer.ProgramStatus.Select(r => r.Id));

        buffer.Process(Report("state=clear"));
        Assert.Empty(buffer.ProgramStatus);
    }

    [Theory]
    [InlineData("kind=permission")]                        // state が無い
    [InlineData("state=sleeping")]                         // 知らない state
    [InlineData("state=idle:id=a//b")]                     // 空の段
    [InlineData("state=idle:id=a b")]                      // 使えない文字
    [InlineData("state=idle:id=1/2/3/4/5/6/7/8/9")]        // 8 段を超える
    [InlineData("state=idle:msg=@@@")]                     // base64 でない
    [InlineData("state=idle:app=0123456789012345678901234567890123")]   // app が 32 バイトを超える
    public void Broken_reports_are_ignored_whole(string body)
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        buffer.Process(Report(body));
        Assert.Empty(buffer.ProgramStatus);
    }

    [Fact]
    public void Messages_with_control_characters_are_refused()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        buffer.Process(Report($"state=idle:msg={B64("evil\u001b[31m")}"));
        Assert.Empty(buffer.ProgramStatus);
    }

    [Fact]
    public void Kind_and_progress_only_apply_where_the_spec_allows()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        buffer.Process(Report("state=done:kind=permission:progress=50"));
        var record = Assert.Single(buffer.ProgramStatus);
        Assert.Equal(ProgramStatusKind.None, record.Kind);
        Assert.Null(record.Progress);

        buffer.Process(Report("state=working:progress=150"));
        Assert.Null(buffer.ProgramStatus.Single().Progress);
    }

    [Fact]
    public void Next_prompt_drops_working_blocked_and_idle_but_keeps_done_and_error()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        buffer.Process(Report("state=working:id=a"));
        buffer.Process(Report("state=blocked:id=b"));
        buffer.Process(Report("state=done:id=c"));
        buffer.Process(Report("state=error:id=d"));
        buffer.Process(Report("state=idle:id=e"));

        buffer.Process($"{Esc}]133;A\u0007");

        Assert.Equal(["c", "d"], buffer.ProgramStatus.Select(r => r.Id));
    }

    [Fact]
    public void Process_exit_drops_working_and_blocked()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        buffer.Process(Report("state=blocked:kind=auth"));
        buffer.DropTransientProgramStatus();
        Assert.Empty(buffer.ProgramStatus);
    }

    [Fact]
    public void Repeating_the_same_report_is_not_a_change()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        var raised = 0;
        buffer.ProgramStatusChanged += (_, _) => raised++;
        buffer.Process(Report("state=working:app=claude-code"));
        buffer.Process(Report("state=working:app=claude-code"));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Query_is_answered_with_a_bare_question_mark()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        var replies = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => replies.Add(text);

        buffer.Process($"{Esc}]7501;?{Esc}\\");

        Assert.Equal([$"{Esc}]7501;?{Esc}\\"], replies);
        Assert.Empty(buffer.ProgramStatus);
    }

    [Fact]
    public void Snapshot_carries_the_records_to_a_reattached_view()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        buffer.Process($"{Esc}]133;A\u0007$ claude\r\n");
        buffer.Process(Report("state=working:id=job:app=claude-code"));
        buffer.Process(Report($"state=blocked:id=job/ask:kind=permission:title={B64("承認")}"));

        var restored = new AnsiTerminalBuffer(80, 10);
        restored.Process(buffer.CreateVtSnapshot([], null, null, null));

        Assert.Equal(buffer.ProgramStatus.Select(r => (r.Id, r.State, r.Kind, r.App, r.Title)),
            restored.ProgramStatus.Select(r => (r.Id, r.State, r.Kind, r.App, r.Title)));
    }
}
