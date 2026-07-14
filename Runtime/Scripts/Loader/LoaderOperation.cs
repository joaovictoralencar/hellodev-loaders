using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace HelloDev.Loader
{
    public class LoaderOperation<T>
    {
        private readonly Func<IProgress<float>, CancellationToken, UniTask<T>> _factory;
        private Action<float> _onProgress;
        private Action<T> _onComplete;
        private Action<string> _onStart;
        private CancellationToken _token;

        internal LoaderOperation(Func<IProgress<float>, CancellationToken, UniTask<T>> factory)
            => _factory = factory;

        public LoaderOperation<T> Progress(Action<float> onProgress) { _onProgress = onProgress; return this; }
        public LoaderOperation<T> OnComplete(Action<T> onComplete)   { _onComplete = onComplete; return this; }
        public LoaderOperation<T> OnStart(Action<string> onStart)    { _onStart = onStart; return this; }

        /// <summary>Attaches a cancellation token. Cancelling aborts the underlying UniTask.</summary>
        public LoaderOperation<T> WithCancellation(CancellationToken token) { _token = token; return this; }

        public UniTask<T>.Awaiter GetAwaiter() => RunAsync().GetAwaiter();
        public void Forget() => RunAsync().Forget();

        private async UniTask<T> RunAsync()
        {
            var progress = _onProgress != null ? new Progress<float>(_onProgress) : null;
            var result = await _factory(progress, _token);
            _onComplete?.Invoke(result);
            return result;
        }
    }

    public class LoaderOperation
    {
        private readonly Func<IProgress<float>, CancellationToken, UniTask> _factory;
        private Action<float> _onProgress;
        private Action _onComplete;
        private CancellationToken _token;

        internal LoaderOperation(Func<IProgress<float>, CancellationToken, UniTask> factory)
            => _factory = factory;

        public LoaderOperation Progress(Action<float> onProgress) { _onProgress = onProgress; return this; }
        public LoaderOperation OnComplete(Action onComplete)       { _onComplete = onComplete; return this; }

        /// <summary>Attaches a cancellation token. Cancelling aborts the underlying UniTask.</summary>
        public LoaderOperation WithCancellation(CancellationToken token) { _token = token; return this; }

        public UniTask.Awaiter GetAwaiter() => RunAsync().GetAwaiter();
        public void Forget() => RunAsync().Forget();

        private async UniTask RunAsync()
        {
            var progress = _onProgress != null ? new Progress<float>(_onProgress) : null;
            await _factory(progress, _token);
            _onComplete?.Invoke();
        }
    }
}