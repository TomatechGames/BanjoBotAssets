﻿namespace BanjoBotAssets.PostExporters.Helpers
{
    internal sealed class ConcurrentUniqueTransformer<TOriginal, TTransformed>
        where TOriginal : notnull
        where TTransformed : notnull
    {
        private readonly Func<TOriginal, TTransformed> transform;
        private readonly Func<TTransformed, TTransformed> mutate;

        private readonly Dictionary<TOriginal, TTransformed> seenInputs;
        private readonly HashSet<TTransformed> seenOutputs;

        /// <summary>
        /// Constructs a new instance of <see cref="ConcurrentUniqueTransformer{TOriginal, TTransformed}"/> using the default
        /// <see cref="EqualityComparer{T}"/> instances.
        /// </summary>
        /// <param name="transform">A delegate to convert a <typeparamref name="TOriginal"/> to a <typeparamref name="TTransformed"/>, which might not be unique.</param>
        /// <param name="mutate">A delegate to modify <typeparamref name="TTransformed"/> to a new value in order to find a unique one.</param>
        public ConcurrentUniqueTransformer(Func<TOriginal, TTransformed> transform, Func<TTransformed, TTransformed> mutate)
            : this(transform, mutate, EqualityComparer<TOriginal>.Default, EqualityComparer<TTransformed>.Default)
        {
        }

        /// <summary>
        /// Constructs a new instance of <see cref="ConcurrentUniqueTransformer{TOriginal, TTransformed}"/> using the default
        /// <see cref="EqualityComparer{T}"/> instances.
        /// </summary>
        /// <param name="transform">A delegate to convert a <typeparamref name="TOriginal"/> to a <typeparamref name="TTransformed"/>, which might not be unique.</param>
        /// <param name="mutate">A delegate to modify <typeparamref name="TTransformed"/> to a new value in order to find a unique one.</param>
        /// <param name="inputComparer">The <see cref="IEqualityComparer{T}"/> to use for comparing <typeparamref name="TOriginal"/>.</param>
        /// <param name="outputComparer">The <see cref="IEqualityComparer{T}"/> to use for comparing <typeparamref name="TTransformed"/>.</param>
        public ConcurrentUniqueTransformer(Func<TOriginal, TTransformed> transform, Func<TTransformed, TTransformed> mutate,
            IEqualityComparer<TOriginal> inputComparer, IEqualityComparer<TTransformed> outputComparer)
        {
            this.transform = transform;
            this.mutate = mutate;

            seenInputs = new(inputComparer);
            seenOutputs = new(outputComparer);
        }

        /// <summary>
        /// Transforms <paramref name="original"/> to a unique <paramref name="transformed"/> value.
        /// </summary>
        /// <param name="original">The value to be transformed.</param>
        /// <param name="transformed">The transformed value. This will be the same value each time this method is called with the same <paramref name="original"/>,
        /// and a different value for each different <paramref name="original"/>.</param>
        /// <returns><see langword="true"/> the first time a particular <paramref name="original"/> is passed in, and <see langword="false"/> every subsequent time.</returns>
        public bool TryTransformIfNovel(TOriginal original, out TTransformed transformed)
        {
            lock (seenInputs)
            {
                // if we've already seen it, we're done
                if (seenInputs.TryGetValue(original, out var result))
                {
                    transformed = result;
                    return false;
                }

                // transform it
                transformed = transform(original);

                while (!seenOutputs.Add(transformed))
                {
                    transformed = mutate(transformed);
                }

                seenInputs.Add(original, transformed);
                return true;
            }
        }
    }
}
