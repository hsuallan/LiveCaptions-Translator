using LiveCaptionsTranslator.utils;
using System.Collections.Concurrent; // 建議引入，但用原本的 lock 也可以

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();
        private readonly List<TranslationTask> tasks;

        private (string translatedText, bool isChoke) output;
        public (string translatedText, bool isChoke) Output => output;

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            output = (string.Empty, false);
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText)
        {
            // 這裡不再隨意建立新的 CancellationTokenSource，除非你需要外部強制停止
            // 我們依賴 Worker 內部的超時機制 (就是你提供的 OpenAI 方法裡的 8秒 timeout)
            var newTranslationTask = new TranslationTask(worker, originalText, new CancellationTokenSource());

            lock (_lock)
            {
                tasks.Add(newTranslationTask);
            }

            // 當任務完成時 (無論成功或失敗)，觸發處理
            newTranslationTask.Task.ContinueWith(
                task => ProcessQueue(), 
                TaskScheduler.Default // 使用預設排程器，避免卡住 UI
            );
        }

        // 核心修改：改為輪詢處理，而不是針對單一任務砍殺
        private async Task ProcessQueue()
        {
            while (true)
            {
                TranslationTask? taskToProcess = null;

                lock (_lock)
                {
                    // 1. 如果隊列空了，沒事做
                    if (tasks.Count == 0) return;

                    // 2. 永遠只看「隊伍最前面」的那個人 (FIFO)
                    var headTask = tasks[0];

                    // 3. 如果最前面那個人還沒跑完，我們就不能動作 (必須等他)
                    //    這樣保證了順序性，也不會殺死他
                    if (!headTask.Task.IsCompleted)
                    {
                        return; 
                    }

                    // 4. 如果最前面那個人跑完了，把他抓出來處理
                    taskToProcess = headTask;
                }

                // --- 以下邏輯已經離開 lock，避免卡死 ---

                if (taskToProcess != null)
                {
                    try 
                    {
                        // 取得結果 (因為已經 IsCompleted，這裡不會卡住)
                        output = await taskToProcess.Task; 
                        var translatedText = output.translatedText;

                        // 執行原本的 Log 邏輯
                        bool isOverwrite = await Translator.IsOverwrite(taskToProcess.OriginalText);
                        if (!isOverwrite)
                            await Translator.AddLogCard();
                        await Translator.Log(taskToProcess.OriginalText, translatedText, isOverwrite);
                    }
                    catch (Exception ex)
                    {
                        // 處理異常，避免整個 Queue 壞掉
                        Console.WriteLine($"Task Error: {ex.Message}");
                    }
                    finally
                    {
                        // 處理完後，安全地從清單移除
                        lock (_lock)
                        {
                            if (tasks.Count > 0 && tasks[0] == taskToProcess)
                            {
                                tasks.RemoveAt(0);
                            }
                        }
                    }
                    // while 迴圈繼續跑，檢查下一個任務是不是也已經好了 (瞬間輸出)
                }
            }
        }
    }

    public class TranslationTask
    {
        public Task<(string translatedText, bool isChoke)> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }

        public TranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
        }
    }
}