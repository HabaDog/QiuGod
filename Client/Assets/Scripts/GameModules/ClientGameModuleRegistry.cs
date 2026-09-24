using System;
using System.Collections.Generic;

namespace MyDemo.GameModules
{
    public interface IClientGameModule
    {
        string GameMode { get; }
        string SceneName { get; }
        void Prepare();
    }

    public static class ClientGameModuleRegistry
    {
        private static readonly Dictionary<string, IClientGameModule> Modules =
            new Dictionary<string, IClientGameModule>(StringComparer.Ordinal);

        public static void Register(IClientGameModule module)
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            string gameMode = Normalize(module.GameMode);
            if (string.IsNullOrEmpty(gameMode))
            {
                throw new ArgumentException("A client game module requires a game mode.", nameof(module));
            }

            if (string.IsNullOrWhiteSpace(module.SceneName))
            {
                throw new ArgumentException("A client game module requires a scene name.", nameof(module));
            }

            Modules[gameMode] = module;
        }

        public static bool TryGet(string gameMode, out IClientGameModule module)
        {
            if (Modules.TryGetValue(Normalize(gameMode), out var found) && found != null)
            {
                module = found;
                return true;
            }

            module = default!;
            return false;
        }

        private static string Normalize(string gameMode)
        {
            return string.IsNullOrWhiteSpace(gameMode)
                ? string.Empty
                : gameMode.Trim().ToLowerInvariant();
        }
    }
}
