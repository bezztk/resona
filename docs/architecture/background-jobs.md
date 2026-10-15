# Download queue

All YouTube audio downloads belong to `ImportQueueService`. The queue is stored
in SQLite and one worker repeatedly claims the oldest queued item, downloads it
with the local `yt-dlp`, persists the track and then continues with the next
item. There is no second scheduler, priority layer or parallel download worker.

Add Track, playlist imports and Channel Hub actions all create entries in this
same queue. The complete `DownloadRequest` is stored with the queue item so a
restart does not lose rating, genre, style or version choices.

Metadata reads may run independently because they do not download audio. Track
analysis also has its own persistent queue and starts only after an audio file
has been saved.
