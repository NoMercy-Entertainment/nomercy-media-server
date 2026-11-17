# Content segments for client teams

A content segment marks a stretch of an episode or a movie: its intro, a
recap, or its credits. The server decides the segments. A client only reads
them and acts on them.

## Where to read them

`GET /api/v1/content-segments/episode/{id}` and
`GET /api/v1/content-segments/movie/{id}` answer:

```json
{
  "data": [ { "segment_type": "Intro", "start_seconds": 5.56, "end_seconds": 82.72,
              "source": "analysis", "confidence": 0.9, "content_end_seconds": null } ],
  "credits_start": 1281.64,
  "content_end": 1377.27
}
```

`segment_type` is `Intro`, `Recap`, `Outro` or `Credits`. Treat `Outro` and
`Credits` the same: both are the end of the item.

## Sources and which one wins

`source` says where a segment came from:

- `manual`: set by a person in the dashboard.
- `chapters`: from the file's chapters, by their title ("OP", "Ending") or by
  their length. A title match has confidence 1.0; a length match less.
- `import`: community data.
- `analysis`: the server measured it from the audio and the picture.
- `detector` and `auto`: older automatic results. They count like `analysis`.

When two segments of one kind cover the same item, the server keeps one.
The order is:

1. manual
2. chapters by title
3. import
4. analysis (and detector, auto)
5. chapters by length

A client does not need to sort. It shows what the list holds.

## Confidence

`confidence` is between 0 and 1.

- 0.5 or more: the client may skip the segment automatically, if the user
  wants that.
- Below 0.5: show a skip button, never skip automatically. This happens when
  credits are written over the last scene of the episode.

## Credits start and content end

- `credits_start` is where the "next episode" button may appear.
- `content_end` is where auto-advance belongs.
- Either one can be null. Without `content_end`, play to the end of the file.

## Content after the credits

Many episodes have story after the credits: an epilogue, a preview of the
next episode, a scene after the copyright card. This is content. The analysis
never puts it in a segment. A client must never skip it. Auto-advance at
`content_end`, not at the end of the credits.
