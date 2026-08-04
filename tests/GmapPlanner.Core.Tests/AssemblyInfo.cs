// Several tests set the process-global TRIP_MAP_DATA_DIR env var to redirect the app
// data dir; running them in parallel makes those writes race. The suite is tiny, so
// serialize it rather than sprinkle collection fixtures.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
