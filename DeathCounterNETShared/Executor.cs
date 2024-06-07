using System.Collections.Concurrent;

namespace DeathCounterNETShared
{
    internal partial class Executor
    {
        Func<Exception, Result>? _defaultExceptionHandler;
        ConcurrentDictionary<Type, Func<Exception, Result>> _customExceptionHandlers;

        public static IDefaultExceptionHandlerRequired GetBuilder() { return ExecutorBuilder.StartBuild(); }
        private Executor()
        {
            _customExceptionHandlers = new();
        }
        public void Execute(Action func)
        {
            try
            {
                func();
            }
            catch (Exception ex)
            {
                HandleExceptions<Nothing>(ex);
            }
        }
        public async Task ExecuteAsync(Func<Task> func)
        {
            try
            {
                await func();
            }
            catch (Exception ex)
            {
                HandleExceptions<Nothing>(ex);
            }
        }
        public Result<TReturnType> Execute<TReturnType>(Func<Result<TReturnType>> func)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                return HandleExceptions<TReturnType>(ex);
            }
        }
        public async Task<Result<TReturnType>> ExecuteAsync<TReturnType>(Func<Task<Result<TReturnType>>> func)
        {
            try
            {
                return await func();
            }
            catch (Exception ex)
            {
                return HandleExceptions<TReturnType>(ex);
            }
        }
        public async Task<Result<TReturnType>>
        RepeatTillMadeItOrTimeout<TReturnType>(Func<Result<TReturnType>> func, int interval, int tryCount)
        {
            Result<TReturnType> res = new BadResult<TReturnType>("something unexpectedly wrong happened");

            while (tryCount > 0)
            {
                try
                {
                    res = func();

                    if (res.IsSuccessful) { return res; }

                    --tryCount;

                    if (tryCount > 0) { await Task.Delay(interval); }
                }
                catch (Exception ex)
                {
                    res = HandleExceptions<TReturnType>(ex);
                }
            }

            return res;
        }
        public async Task<Result<TReturnType>>
        RepeatTillMadeItOrTimeoutAsync<TReturnType>(Func<Task<Result<TReturnType>>> func, int interval, int tryCount)
        {
            Result<TReturnType> res = new BadResult<TReturnType>("something unexpectedly wrong happened");

            while (tryCount > 0)
            {
                try
                {
                    res = await func();

                    if (res.IsSuccessful) { return res; }

                    --tryCount;

                    if (tryCount > 0) { await Task.Delay(interval); }
                }
                catch (Exception ex)
                {
                    res = HandleExceptions<TReturnType>(ex);
                }
            }

            return res;
        }
        private Result<TReturnType> HandleExceptions<TReturnType>(Exception ex)
        {
            foreach (var (type, handler) in _customExceptionHandlers)
            {
                if (ex.GetType() == type)
                {
                    return new Result<TReturnType>(handler(ex));
                }
            }

            Logger.AddToLogs(ex.ToString());

            return new Result<TReturnType>(_defaultExceptionHandler!(ex));
        }
    }

    internal partial class Executor
    {
        public interface IDefaultExceptionHandlerRequired
        {
            IBuildable SetDefaultExceptionHandler(Func<Exception, Result> handler);
        }
        public interface IBuildable
        {
            IBuildable SetCustomExceptionHandler<TExceptionType>(Func<Exception, Result> handler)
                where TExceptionType : Exception;
            Executor Build();
        }
        class ExecutorBuilder : IDefaultExceptionHandlerRequired, IBuildable
        {
            Executor _executor;
            private ExecutorBuilder()
            {
                _executor = new Executor();
            }
            public static IDefaultExceptionHandlerRequired StartBuild()
            {
                return new ExecutorBuilder();
            }
            public IBuildable SetDefaultExceptionHandler(Func<Exception, Result> handler)
            {
                _executor._defaultExceptionHandler = handler;
                return this;
            }
            public IBuildable SetCustomExceptionHandler<TExceptionType>(Func<Exception, Result> handler)
                where TExceptionType : Exception
            {
                _executor._customExceptionHandlers[typeof(TExceptionType)] = handler;
                return this;
            }
            public Executor Build()
            {
                return _executor;
            }
        }
    }
}
