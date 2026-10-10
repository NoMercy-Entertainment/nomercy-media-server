// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

namespace NoMercy.Setup.Server;

public class ApiKeyStore : IApiKeyStore
{
    private static IApiKeyStore? _instance;

    // Lazily create a default (empty-keys) store if nothing has constructed one yet, so
    // early access and the separate Launcher process never crash. The DI singleton replaces
    // this during boot (its ctor sets _instance) and ApiKeyLoader then fills in the keys.
    public static IApiKeyStore Current => _instance ??= new ApiKeyStore();

    public ApiKeyStore()
    {
        _instance = this;
    }

    public string AcousticIdKey { get; internal set; } = string.Empty;
    public string FanArtApiKey { get; internal set; } = string.Empty;
    public string FanArtClientKey { get; internal set; } = string.Empty;
    public string MakeMkvKey { get; internal set; } = string.Empty;
    public string MusixmatchKey { get; internal set; } = string.Empty;
    public string OmdbKey { get; internal set; } = string.Empty;
    public string RottenTomatoes { get; internal set; } = string.Empty;
    public string TadbKey { get; internal set; } = string.Empty;
    public string TmdbKey { get; internal set; } = string.Empty;
    public string TmdbToken { get; internal set; } = string.Empty;
    public string TvdbKey { get; internal set; } = string.Empty;
    public bool KeysLoaded { get; internal set; }

    public string[] Colors { get; } =
    [
        "#CBAFFF",
        "#B5A0FF",
        "#9F91FF",
        "#8982FF",
        "#7373FF",
        "#5D64FF",
        "#4755FF",
        "#3146FF",
        "#1B37FF",
        "#0528FF",
    ];

    public string Quote { get; } = GetRandomQuote();

    private static string GetRandomQuote()
    {
        string[] quotes =
        [
            "An unexamined life is not worth living. - Socrates",
            "Be present above all else. - Naval Ravikant",
            "Do what you can, with what you have, where you are. - Theodore Roosevelt",
            "Happiness is not something readymade. It comes from your own actions. - Dalai Lama",
            "He who is contented is rich. - Laozi",
            "I have not failed. I've just found 10,000 ways that won't work. - Thomas Edison",
            "It is never too late to be what you might have been. - George Eliot",
            "It is not the man who has too little, but the man who craves more, that is poor. - Seneca",
            "It is quality rather than quantity that matters. - Lucius Annaeus Seneca",
            "Live as if you were to die tomorrow. Learn as if you were to live forever. - Mahatma Gandhi",
            "No surplus words or unnecessary actions. - Marcus Aurelius",
            "Nothing worth having comes easy. - Theodore Roosevelt",
            "Order your soul. Reduce your wants. - Augustine",
            "People find pleasure in different ways. I find it in keeping my mind clear. - Marcus Aurelius",
            "Simplicity is an acquired taste. - Katharine Gerould",
            "Simplicity is the consequence of refined emotions. - Jean D'Alembert",
            "Simplicity is the essence of happiness. - Cedric Bledsoe",
            "Simplicity is the ultimate sophistication. - Leonardo da Vinci",
            "Smile, breathe, and go slowly. - Thich Nhat Hanh",
            "The only way to do great work is to love what you do. - Steve Jobs",
            "The whole future lies in uncertainty: live immediately. - Seneca",
            "Very little is needed to make a happy life. - Marcus Aurelius",
            "Waste no more time arguing what a good man should be, be one. - Marcus Aurelius",
            "Well begun is half done. - Aristotle",
            "When there is no desire, all things are at peace. - Laozi",
            "Walk as if you are kissing the Earth with your feet. - Thich Nhat Hanh",
            "Because you are alive, everything is possible. - Thich Nhat Hanh",
            "Breathing in, I calm body and mind. Breathing out, I smile. - Thich Nhat Hanh",
            "Life is available only in the present moment. - Thich Nhat Hanh",
            "The biggest battle is the war against ignorance. - Mustafa Kemal Atatürk",
            "Always remember that you are absolutely unique. Just like everyone else. - Margaret Mead",
            "You must be the change you wish to see in the world. - Mahatma Gandhi",
        ];

        return quotes[new Random().Next(quotes.Length)];
    }
}
