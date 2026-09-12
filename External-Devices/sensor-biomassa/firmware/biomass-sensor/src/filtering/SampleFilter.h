void resetReadingFilter() {
  g_filterIsPrimed     = false;
  g_readingWindowIndex = 0;
  for (int i = 0; i < FILTER_WINDOW_SIZE; i++) {
    g_readingWindow[i] = 0;
  }
  g_emaFilterIsPrimed = false;
}

uint16_t getFilteredReading(uint16_t newReading) {
  if (!g_filterIsPrimed) {
    for (int i = 0; i < FILTER_WINDOW_SIZE; i++) {
      g_readingWindow[i] = newReading;
    }
    g_filterIsPrimed = true;
    return newReading;
  }

  g_readingWindow[g_readingWindowIndex] = newReading;
  g_readingWindowIndex = (g_readingWindowIndex + 1) % FILTER_WINDOW_SIZE;

  uint16_t sortedWindow[FILTER_WINDOW_SIZE];
  memcpy(sortedWindow, g_readingWindow, sizeof(g_readingWindow));
  std::sort(sortedWindow, sortedWindow + FILTER_WINDOW_SIZE);

  return sortedWindow[FILTER_WINDOW_SIZE / 2];
}

