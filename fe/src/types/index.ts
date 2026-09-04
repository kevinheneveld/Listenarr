/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
export interface BaseSearchResult {
  id: string
  title: string
  artist: string
  album: string
  category: string
  source: string
  sourceLink?: string
  publishedDate: string
  format: string
  score?: number
}

export interface OpenLibraryBook {
  key: string
  title: string
  // OpenLibrary sometimes returns `author_name` as an array, other times a single string
  author_name?: string[] | string
  author_key?: string[]
  first_publish_year?: number
  isbn?: string[]
  edition_key?: string[]
  cover_edition_key?: string
  publisher?: string[]
  cover_i?: number
  edition_count?: number
  language?: string[]
  subject?: string[]
  ebook_access?: 'public' | 'borrowable' | 'printdisabled' | 'no_ebook'
  has_fulltext?: boolean
  public_scan_b?: boolean
  seriesList?: string[]
}

export interface IndexerSearchResult extends BaseSearchResult {
  downloadReference?: string
  size: number
  seeders?: number
  leechers?: number
  magnetLink: string
  torrentUrl: string
  nzbUrl: string
  downloadType: string // "Torrent", "Usenet", or "DDL"
  quality?: string
  resultUrl?: string // Canonical indexer page for the result
}

export interface MetadataSearchResult extends BaseSearchResult {
  description?: string
  subtitle?: string
  publisher?: string
  language?: string
  runtime?: number
  narrator?: string
  imageUrl?: string
  asin?: string
  isbn?: string
  series?: string
  seriesNumber?: string
  seriesAsin?: string
  seriesList?: string[]
  genres?: string[] // Genres from metadata sources (e.g., Audible)
  productUrl?: string // Direct link to Amazon/Audible product page
  isEnriched?: boolean
  metadataSource?: string // Which metadata API enriched this result
  // Audible-style fields (when backend returns Audible-shaped JSON)
  authors?: AudibleAuthor[]
  narrators?: AudibleNarrator[]
  lengthMinutes?: number
  link?: string
  releaseDate?: string
  publishDate?: string
}

// Legacy SearchResult interface - kept for backwards compatibility
// Combines both indexer and metadata properties
export interface SearchResult extends BaseSearchResult {
  downloadReference?: string
  // Indexer-specific properties
  thumbnailRetentionDays?: number
  size: number
  seeders?: number
  leechers?: number
  grabs?: number
  files?: number
  magnetLink: string
  torrentUrl: string
  nzbUrl: string
  downloadType: string // "Torrent", "Usenet", or "DDL"
  quality?: string
  resultUrl?: string // Canonical indexer page for the result

  // Metadata-specific properties
  description?: string
  subtitle?: string
  publisher?: string
  language?: string
  runtime?: number
  narrator?: string
  imageUrl?: string
  asin?: string
  isbn?: string
  series?: string
  seriesNumber?: string
  seriesAsin?: string
  seriesList?: string[]
  genres?: string[] // Genres from metadata sources (e.g., Audible)
  productUrl?: string // Direct link to Amazon/Audible product page
  isEnriched?: boolean
  metadataSource?: string // Which metadata API enriched this result
  // Audible-style fields
  authors?: AudibleAuthor[]
  narrators?: AudibleNarrator[]
  lengthMinutes?: number
  link?: string
  releaseDate?: string
  publishDate?: string
}

export interface DashboardStatsResponse {
  quality: {
    byCodec: { codec: string; count: number }[]
    byBitrate: { label: string; count: number }[]
  }
  completeness: {
    totalBooks: number
    missingCoverArt: number
    missingDescription: number
    missingNarrators: number
    missingSeriesPosition: number
  }
}

export type ActivityCategory = 'InProgress' | 'Blocked' | 'Imported' | 'Failed' | 'Stalled'

export interface ActivityItem {
  id: string
  audiobookId?: number
  title: string
  artist: string
  series?: string
  category: ActivityCategory
  status: string
  progress: number
  totalSize: number
  downloadedSize: number
  startedAt: string
  completedAt?: string
  // CompletedAt ?? StartedAt — the sort/window key
  activityAt: string
  reason?: string
  // How many download records collapsed into this row (repeated grab attempts)
  attemptCount: number
  downloadClientId: string
  downloadClientName?: string
  // Download client implementation ("qbittorrent", "nzbget", "DDL", ...) for the client-type indicator.
  downloadClientType?: string
  // Per-attempt breakdown of the grab attempts that collapsed into this row, newest-first.
  attempts?: ActivityAttempt[]
}

// One historical grab attempt for a book (GET /downloads/activity).
export interface ActivityAttempt {
  category: ActivityCategory
  status: string
  at: string
  reason?: string
  downloadClientName?: string
}

export interface ActivityReasonCount {
  reason: string
  count: number
}

export interface ActivitySummaryCounts {
  inProgress: number
  blocked: number
  imported: number
  failed: number
  stalled: number
  windowHours: number
  failureReasons: ActivityReasonCount[]
}

export interface ActivityResponse {
  summary: ActivitySummaryCounts
  items: ActivityItem[]
  totalItems: number
}

export interface Download {
  id: string
  title: string
  artist: string
  album: string
  originalUrl: string
  status:
    | 'Queued'
    | 'Downloading'
    | 'Paused'
    | 'Completed'
    | 'Failed'
    | 'Processing'
    | 'Ready'
    | 'Moved'
    | 'ImportPending'
    | 'ImportBlocked'
  progress: number
  totalSize: number
  downloadedSize: number
  downloadPath: string
  finalPath: string
  startedAt: string
  completedAt?: string
  errorMessage?: string
  downloadClientId: string
  metadata: Record<string, unknown>
  // Optional link to an audiobook record when the download was queued for a specific audiobook
  audiobookId?: number
}

export interface QueueItem {
  id: string
  title: string
  audiobookId?: number
  author?: string
  series?: string
  seriesNumber?: string
  quality: string
  language?: string
  status: string // downloading, paused, queued, completed, failed
  progress: number // 0-100
  size: number // in bytes
  downloaded: number // in bytes
  downloadSpeed: number // bytes per second
  eta?: number // seconds remaining
  indexer?: string
  downloadClient: string
  downloadClientId: string
  downloadClientType: string
  addedAt: string
  errorMessage?: string
  isStaleSnapshot?: boolean
  snapshotState?: string
  snapshotFailureReason?: string
  snapshotAgeSeconds?: number
  snapshotRefreshedAt?: string
  canPause: boolean
  canRemove: boolean
  seeders?: number
  leechers?: number
  ratio?: number
  remotePath?: string // Path as seen by download client
  localPath?: string // Path translated for Listenarr
}

export interface QueueClientStatus {
  clientId: string
  clientName: string
  clientType: string
  snapshotState: string
  isStaleSnapshot: boolean
  isUnavailable: boolean
  snapshotFailureReason?: string
  snapshotAgeSeconds?: number
  snapshotRefreshedAt?: string
  itemCount: number
}

export interface QueueSnapshot {
  items: QueueItem[]
  clients: QueueClientStatus[]
  generatedAt: string
  hasStaleData: boolean
  hasUnavailableClients: boolean
}

export type QueueUpdatePayload = QueueSnapshot | QueueItem[]

export interface ApiConfiguration {
  id: string
  name: string
  baseUrl: string
  apiKey: string
  type: 'torrent' | 'nzb' | 'metadata' | 'search' | 'other'
  isEnabled: boolean
  priority: number
  headers: Record<string, string>
  parameters: Record<string, string>
  rateLimitPerMinute?: string
  createdAt: string
  lastUsed?: string
}

export interface DownloadClientConfiguration {
  id: string
  name: string
  type: 'qbittorrent' | 'transmission' | 'sabnzbd' | 'nzbget'
  host: string
  port: number
  username: string
  password: string
  downloadPath: string
  useSSL: boolean
  isEnabled: boolean
  removeCompletedDownloads?: string // "none", "remove", "remove_and_delete"
  // Client-specific settings. Use `DownloadClientSettings` for typed access
  settings: DownloadClientSettings
  // Optional persisted last test result (true = success, false = failure)
  lastTestSuccessful?: boolean
}

export interface DownloadClientSettings {
  apiKey?: string
  urlBase?: string
  category?: string
  tags?: string
  recentPriority?: string
  olderPriority?: string
  removeCompleted?: boolean
  removeFailed?: boolean
  initialState?: string
  sequentialOrder?: boolean
  firstAndLastFirst?: boolean
  contentLayout?: string
  // Per-torrent share limits stamped on every grab (qBittorrent):
  // undefined/blank follows the client's global setting.
  seedRatioLimit?: number
  seedTimeLimitMinutes?: number
  // Optional mapping to one or more remote path mapping IDs
  remotePathMappingIds?: number[]
  [key: string]: unknown
}

export interface RemotePathMapping {
  id: number
  downloadClientId: string
  name?: string
  remotePath: string
  localPath: string
  createdAt: string
  updatedAt: string
}

export interface RootFolder {
  id: number
  name: string
  path: string
  pathSyntax?: 'Windows' | 'Unix' | null
  isDefault: boolean
  createdAt?: string
  updatedAt?: string | null
  caseSensitivityMode?: 'Auto' | 'Sensitive' | 'Insensitive'
  resolvedCaseSensitivity?: 'Unknown' | 'Sensitive' | 'Insensitive'
  pathIdentityState?: 'Valid' | 'Conflict' | 'Unavailable'
  storageState?:
    | 'Healthy'
    | 'Limited'
    | 'Missing'
    | 'Changed'
    | 'Unavailable'
    | 'Unconfirmed'
    | 'Initializing'
    | 'InitializationFailed'
  storageReason?:
    | 'None'
    | 'PathMissing'
    | 'ForeignPathSyntax'
    | 'AccessDenied'
    | 'IdentityUnsupported'
    | 'IdentityMismatch'
    | 'IdentityUnstable'
    | 'FilesystemSemanticsUnavailable'
    | 'FilesystemSemanticsChanged'
    | 'MutationSemanticsUnproven'
    | 'ReadOnlyFilesystem'
    | 'MutationCapabilityUnavailable'
    | 'NoAuthorizedIdentity'
    | 'InvalidPath'
    | 'Initializing'
    | 'InitializationFailed'
    | 'Unknown'
  storageMessage?: string | null
  storageDetail?: string | null
  canConfirmCurrentFolder?: boolean
  canChangePath?: boolean
  canReadFilesystem?: boolean
  canScanFilesystem?: boolean
  canPublishNewFiles?: boolean
  canMutateFilesystem?: boolean
  canRetireWithDurableIdentity?: boolean
  canRetireAfterVerifiedCopy?: boolean
  weakStorageSourceCleanupPolicy?: 'RetainSource' | 'DeleteSourceAfterVerifiedCopy'
  weakStoragePolicyRevision?: number
  confirmationToken?: string | null
  activeRelocation?: RootFolderPathChangeResult | null
}

export interface AudiobookDeleteCapabilities {
  canRemoveFromLibrary: boolean
  canDeleteTrackedFiles: boolean
  canDeleteFolder: boolean
  reason?: string | null
  fallbackAction: 'RemoveFromLibraryOnly'
}

export interface WeakStorageMissingFileCandidate {
  id: string
  audiobookFileId: number
  path: string
}

export interface WeakStorageMissingFilesResponse {
  scanToken?: string | null
  expiresAt?: string | null
  items: WeakStorageMissingFileCandidate[]
}

export type RootFolderRelocationSkipReasonCode =
  | 'Unknown'
  | 'InvalidStoredPath'
  | 'SourceSemanticsUnavailable'
  | 'TargetPathInvalid'
  | 'TargetIdentityCollision'
  | 'TargetIdentityUnresolvedConflict'

export interface RootFolderRelocationSkippedItem {
  audiobookId: number
  reasonCode: RootFolderRelocationSkipReasonCode
}

export interface RootFolderMetadataRepairCollisionFile {
  audiobookFileId: number
  audiobookId: number
  relativePath: string
  canRemove: boolean
}

export interface RootFolderMetadataRepairCollisionGroup {
  targetRelativePath: string
  files: RootFolderMetadataRepairCollisionFile[]
}

export interface RootFolderMetadataRepairDetails {
  relocationId: string
  audiobookId: number
  audiobookTitle: string
  reasonCode: RootFolderRelocationSkipReasonCode
  collisionGroups: RootFolderMetadataRepairCollisionGroup[]
}

export interface RootFolderPathChangeResult {
  relocationId?: string | null
  rootFolderId: number | null
  currentPath: string
  targetPath: string
  status: 'Pending' | 'Running' | 'NeedsAttention' | 'Completed' | 'Failed'
  totalJobs: number
  completedJobs: number
  error?: string | null
  targetIdentityEnrollmentState: 'NotRequired' | 'Authorized' | 'Unavailable'
  skippedAudiobookIds?: number[] | null
  mode?: 'Relocate' | 'MetadataOnly'
  skippedItems?: RootFolderRelocationSkippedItem[] | null
  canAbandon?: boolean
}

export interface TranslatePathRequest {
  downloadClientId: string
  remotePath: string
}

export interface TranslatePathResponse {
  downloadClientId: string
  remotePath: string
  localPath: string
  translated: boolean
}

export interface ApplicationSettings {
  /**
   * Optimistic-concurrency token. The backend rejects a save whose version
   * doesn't match the persisted row (409 settings_concurrency_conflict), so
   * after every save the local editing copy must adopt the response's
   * version or the SECOND save without a page reload fails.
   */
  version?: number
  verificationOpeningSeconds?: number
  verificationClosingSeconds?: number
  verificationOnImport?: boolean
  verificationLowCpuPriority?: boolean
  verificationAutoRejectWrongContent?: boolean
  verificationEscalationModel?: string
  aiAssistEnabled?: boolean
  aiAssistBaseUrl?: string
  aiAssistModel?: string
  aiAssistApiKey?: string
  aiAssistGateSearches?: boolean
  outputPath: string
  folderNamingPattern: string
  fileNamingPattern: string
  multiFileNamingPattern: string
  enableMetadataProcessing: boolean
  enableCoverArtDownload: boolean
  audnexusApiUrl: string
  maxConcurrentDownloads: number
  unmatchedScanConcurrency?: number
  pollingIntervalSeconds?: number
  // How many seconds a download must be observed as complete by the client before finalization begins
  downloadCompletionStabilitySeconds?: number
  // Retry/backoff settings used by the server when a finalized download's source file is not yet present
  missingSourceRetryInitialDelaySeconds?: number
  missingSourceMaxRetries?: number
  enableNotifications: boolean
  allowedFileExtensions: string[]
  importBlacklistExtensions?: string[]
  // Action to perform for completed downloads.
  completedFileAction?: 'none' | 'move' | 'copy' | 'hardlink/copy'
  // Show completed external downloads (torrents/NZBs) in the Activity view
  showCompletedExternalDownloads?: boolean
  // Background "fill missing from online": look up books that hold files but lack
  // core metadata by their own ASIN/ISBN and fill only the blank fields.
  metadataAutoBackfillEnabled?: boolean
  // Failed download handling
  failedDownloadHandlingEnabled?: boolean
  failedDownloadAutoSearch?: boolean
  // Optional admin credentials used when saving settings to create/update an initial admin user
  adminUsername?: string
  adminPassword?: string

  // Notification settings
  webhookUrl?: string
  enabledNotificationTriggers?: string[]
  // New webhook format (multiple webhooks)
  webhooks?: Array<{
    id: string
    name: string
    url: string
    type: 'Pushbullet' | 'Telegram' | 'Slack' | 'Discord' | 'Pushover' | 'NTFY' | 'Zapier'
    triggers: string[]
    isEnabled: boolean
  }>

  // Discord bot integration settings (optional)
  discordBotEnabled?: boolean
  discordApplicationId?: string
  discordGuildId?: string
  // Optional Discord channel id to restrict commands to a single channel
  discordChannelId?: string
  // Stored token (if provided via Settings). Note security implications.
  discordBotToken?: string
  // Command group and subcommand names, resulting in `/group subcommand` usage
  discordCommandGroupName?: string
  discordCommandSubcommandName?: string
  // Optional bot appearance customization
  discordBotUsername?: string
  discordBotAvatar?: string

  // Search behavior settings
  // Enable OpenLibrary augmentation/search
  enableOpenLibrarySearch?: boolean
  defaultSearchRegion?: string
  defaultSearchLanguage?: string
}

export interface ProwlarrImportConnectionSettings {
  url: string
  port?: number
  tagFilter?: string
  hasSavedApiKey: boolean
}

export interface AudiobookshelfConnectionSettings {
  url: string
  apiKey?: string | null
  libraryId?: string | null
  notifyOnImport: boolean
  hasSavedApiKey: boolean
}

export interface AudiobookshelfLibrary {
  id: string
  name: string
  mediaType: string
}

export interface AudiobookshelfActionResult {
  success: boolean
  message: string
  libraries?: AudiobookshelfLibrary[] | null
}

export interface StartupConfig {
  logLevel?: string
  enableSsl?: boolean
  port?: number
  sslPort?: number
  urlBase?: string
  bindAddress?: string
  apiKey?: string
  authenticationMethod?: string
  updateMechanism?: string
  launchBrowser?: boolean
  branch?: string
  instanceName?: string
  syslogPort?: number
  analyticsEnabled?: boolean
  authenticationRequired?: string | boolean
  // PascalCase variant is accepted for compatibility with some server responses
  AuthenticationRequired?: string | boolean
  apiVersion?: string | number
  ApiVersion?: string | number
  sslCertPath?: string
  sslCertPassword?: string
}

export interface StartupConfigDto {
  authenticationRequired?: string | boolean
  AuthenticationRequired?: string | boolean
  apiVersion?: string | number
  ApiVersion?: string | number
}

export interface AudibleBookMetadata {
  title: string
  subtitle?: string
  authors: string[]
  publishedDate?: string
  publishYear?: string
  series?: string
  seriesNumber?: string
  seriesAsin?: string
  seriesMemberships?: AudiobookSeriesMembership[]
  seriesList?: string[]
  description?: string
  genres?: string[]
  tags?: string[]
  narrators?: string[]
  isbn?: string
  asin: string
  searchResult?: SearchResult
  publisher?: string
  language?: string
  runtime?: number
  edition?: string
  version?: string
  imageUrl?: string
  explicit?: boolean
  abridged?: boolean
  source?: string
  sourceLink?: string
  region?: string
  openLibraryId?: string
  metadataSource?: string
  // Optional local mapping to a quality profile ID when viewing in the UI
  qualityProfileId?: number
}

export interface AuthorCatalogBook {
  asin?: string
  title: string
  subtitle?: string
  authors?: string[]
  imageUrl?: string
  runtime?: number
  language?: string
  publisher?: string
  narrators?: string[]
  genres?: string[]
  series?: string
  seriesNumber?: string
  publishedDate?: string
  isbn?: string
  link?: string
  metadataSource?: string
}

export interface AuthorCatalogResponse {
  author: {
    asin?: string
    name: string
    image?: string
  }
  books: AuthorCatalogBook[]
  totalBooks: number
}

export interface RelatedAuthorItem {
  asin?: string
  name: string
}

export interface AuthorLookupResponse {
  asin?: string
  name: string
  image?: string
  cachedPath?: string
  description?: string
  similarAuthors?: RelatedAuthorItem[]
}

export interface SeriesCatalogBook {
  asin?: string
  title: string
  subtitle?: string
  authors?: string[]
  imageUrl?: string
  runtime?: number
  language?: string
  publisher?: string
  narrators?: string[]
  genres?: string[]
  series?: string
  seriesNumber?: string
  publishedDate?: string
  isbn?: string
  link?: string
  metadataSource?: string
}

export interface SeriesCatalogResponse {
  series: {
    asin?: string
    name: string
    image?: string
    description?: string
  }
  books: SeriesCatalogBook[]
  totalBooks: number
}

export interface SeriesLookupResponse {
  asin?: string
  name: string
  image?: string
  cachedPath?: string
  description?: string
  totalBooks?: number
}

export interface MonitoredAuthor {
  id: number
  authorName: string
  authorAsin?: string
  region: string
  language: string
  createdAt: string
  updatedAt: string
  lastCheckedAt?: string
  lastSuccessfulSyncAt?: string
  lastError?: string
}

export interface AuthorMonitoringStatusResponse {
  isMonitored: boolean
  monitoredAuthor?: MonitoredAuthor | null
}

export interface MonitorAuthorResponse {
  message: string
  monitoredAuthor: MonitoredAuthor
  addedCount: number
  existingCount: number
  failedCount: number
  errorMessage?: string
}

export interface MonitoredSeries {
  id: number
  seriesName: string
  seriesAsin?: string
  /** True when the user pinned this ASIN via the "Wrong series?" picker. */
  asinPinned?: boolean
  region: string
  language: string
  createdAt: string
  updatedAt: string
  lastCheckedAt?: string
  lastSuccessfulSyncAt?: string
  lastError?: string
}

export interface SeriesMonitoringStatusResponse {
  isMonitored: boolean
  monitoredSeries?: MonitoredSeries | null
}

export interface MonitorSeriesResponse {
  message: string
  /** True when a repoint collapsed this series into an existing monitored entry. */
  merged?: boolean
  monitoredSeries: MonitoredSeries
  addedCount: number
  existingCount: number
  failedCount: number
  errorMessage?: string
}

export interface SeriesCandidate {
  asin: string
  name?: string
  image?: string
  bookCount?: number
  /** "library" when derived from a book you own, otherwise "audible". */
  source: 'library' | 'audible'
  ownedMatchCount: number
}

export interface SeriesCandidatesResponse {
  query: string
  bestGuessAsin?: string
  candidates: SeriesCandidate[]
}

export type AudiobookExternalIdentifierType = 'Asin' | 'Isbn' | 'OpenLibraryId'
export type AudiobookExternalIdentifierSource = 'Provider' | 'Imported' | 'Manual'

export interface AudiobookSeriesMembership {
  id?: number
  seriesName: string
  seriesNumber?: string
  seriesAsin?: string
  isPrimary?: boolean
  sortOrder?: number
}

export interface AudiobookExternalIdentifier {
  id: number
  type: AudiobookExternalIdentifierType
  value: string
  valueNormalized: string
  region?: string | null
  isPrimary: boolean
  source: AudiobookExternalIdentifierSource
  createdAt?: string
  updatedAt?: string
}

export interface AudiobookExternalIdentifierInput {
  type: AudiobookExternalIdentifierType
  value: string
  region?: string | null
  isPrimary?: boolean
  source?: AudiobookExternalIdentifierSource
}

export type AudiobookStatus = 'downloading' | 'no-file' | 'quality-mismatch' | 'quality-match'

// Audio-based identity verification (ADR-0001). Manual states are sticky:
// agent passes never overwrite manuallyVerified/rejected. agentUnverifiable is
// the neutral "audio never announces itself" state — not a flag.
export type VerificationStatus =
  | 'unverified'
  | 'agentVerified'
  | 'agentFlagged'
  | 'manuallyVerified'
  | 'rejected'
  | 'agentUnverifiable'

export type VerificationOutcome = 'match' | 'mismatch' | 'uncertain' | 'noSpokenCredits'

// Per-field verdict detail persisted by the agent pass (audiobook.verificationDetailJson).
export interface VerificationFieldMatch {
  score: number
  matchedText?: string | null
}

// What the spoken credits CLAIM the book is, extracted from the opening
// transcript — seeds the "find correct match" relabel flow on flagged books.
export interface SpokenCredits {
  title?: string | null
  author?: string | null
  narrator?: string | null
  publisher?: string | null
}

// On-disk audio length versus catalog runtime — distinguishes "partial content
// of the right book" from genuinely wrong audio.
export interface VerificationCompleteness {
  expectedMinutes: number
  actualMinutes: number
  coverage: number
}

export interface VerificationDetail {
  outcome: VerificationOutcome
  confidence: number
  method: string
  titleMatch?: VerificationFieldMatch | null
  authorMatch?: VerificationFieldMatch | null
  narratorMatch?: VerificationFieldMatch | null
  publisherMatch?: VerificationFieldMatch | null
  heardCredits?: SpokenCredits | null
  completeness?: VerificationCompleteness | null
}

export interface Audiobook {
  id: number
  title: string
  subtitle?: string
  authors?: string[]
  publishedDate?: string
  publishYear?: string
  series?: string
  seriesNumber?: string
  seriesMemberships?: AudiobookSeriesMembership[]
  description?: string
  genres?: string[]
  tags?: string[]
  narrators?: string[]
  isbn?: string
  asin?: string
  openLibraryId?: string
  publisher?: string
  language?: string
  runtime?: number
  edition?: string
  version?: string
  imageUrl?: string
  explicit?: boolean
  abridged?: boolean
  monitored?: boolean
  filePath?: string
  fileSize?: number
  fileCount?: number
  basePath?: string
  files?: {
    id: number
    path?: string
    size?: number
    durationSeconds?: number
    format?: string
    container?: string
    codec?: string
    bitrate?: number
    sampleRate?: number
    channels?: number
    createdAt?: string
    source?: string
  }[]
  verificationStatus?: VerificationStatus
  /** When the book most recently had a file imported (max file CreatedAt); null when no tracked files. */
  importedAt?: string
  verificationConfidence?: number | null
  verifiedAt?: string | null
  verifiedBy?: string | null
  verificationMethod?: string | null
  verificationTranscript?: string | null
  verificationDetailJson?: string | null
  quality?: string
  qualityProfileId?: number
  // Optional list of author ASINs (populated by backend when available)
  authorAsins?: string[]
  identifiers?: AudiobookExternalIdentifier[]
  // Server-computed flag indicating if this audiobook is wanted (monitored and missing files)
  wanted?: boolean
  // Server-computed list status used by slim /library responses.
  status?: AudiobookStatus
}

export interface AudiobookUpdateRequest {
  title?: string
  subtitle?: string
  authors?: string[]
  imageUrl?: string
  publishYear?: string
  publishedDate?: string
  series?: string
  seriesNumber?: string
  seriesMemberships?: AudiobookSeriesMembership[]
  description?: string
  genres?: string[]
  tags?: string[]
  narrators?: string[]
  isbn?: string[]
  asin?: string
  openLibraryId?: string
  publisher?: string
  language?: string
  runtime?: number
  edition?: string
  version?: string
  explicit?: boolean
  abridged?: boolean
  monitored?: boolean
  filePath?: string
  fileSize?: number
  basePath?: string
  quality?: string
  qualityProfileId?: number
}

export interface History {
  id: number
  audiobookId?: number
  audiobookTitle?: string
  eventType: string
  message?: string
  source?: string
  timestamp: string
  notificationSent?: boolean
  data?: string
}

export interface Indexer {
  id: number
  name: string
  type: string // "Torrent" or "Usenet"
  implementation: string // "Newznab", "Torznab", "Custom"
  url: string
  apiKey?: string
  categories?: string
  animeCategories?: string
  tags?: string
  enableRss: boolean
  enableAutomaticSearch: boolean
  enableInteractiveSearch: boolean
  enableAnimeStandardSearch: boolean
  isEnabled: boolean
  priority: number
  minimumAge: number
  retention: number
  maximumSize: number
  additionalSettings?: string
  createdAt: string
  updatedAt: string
  lastTestedAt?: string
  lastTestSuccessful?: boolean
  lastTestError?: string
}

export interface SystemReadiness {
  isReady: boolean
  status: string
  databaseConnected: boolean
  migrationsCurrent: boolean
  errorCode?: string | null
  filesystemReady: boolean
  filesystemStatus: 'Pending' | 'Running' | 'Ready' | 'Failed'
  filesystemPhase?: string | null
  filesystemErrorCode?: string | null
  filesystemErrorMessage?: string | null
}

export interface SystemInfo {
  version: string
  operatingSystem: string
  runtime: string
  uptime: string
  memory: MemoryInfo
  cpu: CpuInfo
  startTime: string
}

export interface MemoryInfo {
  usedBytes: number
  totalBytes: number
  freeBytes: number
  usedPercentage: number
  usedFormatted: string
  totalFormatted: string
  freeFormatted: string
}

export interface CpuInfo {
  usagePercentage: number
  processorCount: number
}

export interface StorageInfo {
  usedBytes: number
  totalBytes: number
  freeBytes: number
  usedPercentage: number
  usedFormatted: string
  totalFormatted: string
  freeFormatted: string
  driveName: string
  status: string
  disks: DiskStorageInfo[]
}

export interface DiskStorageInfo {
  label: string
  path: string
  usedBytes: number
  totalBytes: number
  freeBytes: number
  usedPercentage: number
  usedFormatted: string
  totalFormatted: string
  freeFormatted: string
  status: string
}

export interface ServiceHealth {
  status: string // "healthy", "warning", "error", "unknown"
  version: string
  uptime: string
  downloadClients: DownloadClientHealth
  externalApis: ExternalApiHealth
}

export interface DownloadClientHealth {
  status: string
  connected: number
  total: number
  clients: ClientStatus[]
}

export interface ExternalApiHealth {
  status: string
  connected: number
  total: number
  apis: ApiStatus[]
}

export interface ClientStatus {
  name: string
  status: string // "connected", "disconnected", "unknown"
  type?: string
}

export interface ApiStatus {
  name: string
  status: string // "connected", "disconnected", "unknown"
  enabled: boolean
}

export interface LogEntry {
  id: string
  timestamp: string // ISO date string
  level: string // "Info", "Warning", "Error", "Debug"
  message: string
  exception?: string
  source?: string
}

export interface QualityProfile {
  id?: number
  name: string
  description?: string
  qualities: QualityDefinition[]
  cutoffQuality?: string
  minimumSize?: number // MB (optional - no minimum if not set)
  maximumSize?: number // MB (optional - no maximum if not set)
  preferredFormats?: string[] // e.g., ["m4b", "mp3", "m4a", "flac", "opus"]
  preferredWords?: string[] // Words that increase score
  mustNotContain?: string[] // Instant rejection
  mustContain?: string[] // Must be present
  preferredLanguages?: string[] // e.g., ["English", "Spanish"]
  minimumSeeders?: number
  minimumScore?: number // Minimum score threshold for automatic downloads
  isDefault?: boolean
  preferNewerReleases?: boolean
  maximumAge?: number // days (0 = no limit)
  customGroupNames?: Record<string, string> // Custom names for quality groups by codec
  createdAt?: string
  updatedAt?: string
}

export interface QualityDefinition {
  quality: string // e.g., "320kbps", "192kbps", "lossless"
  allowed: boolean
  priority: number // Lower = higher priority
  codec?: string
  bitrate?: number
  isLossless?: boolean
}

/**
 * Extended quality information for better organization
 * Maps the string identifiers to structured codec/bitrate data
 */
export interface QualityInfo {
  id: string // Unique identifier matching QualityDefinition.quality
  label: string // Display label (e.g., "MP3 320 kbps")
  codec: string // Codec type (MP3, AAC, M4B, OPUS, OGG Vorbis, FLAC)
  bitrate?: number // Bitrate in kbps (optional for lossless)
  isLossless: boolean // Whether codec is lossless
  category: 'lossy' | 'lossless' | 'unknown' // Category for grouping
}

/**
 * Quality group for organizing qualities by category
 */
export interface QualityGroup {
  category: 'lossy' | 'lossless' | 'unknown'
  label: string
  qualities: QualityInfo[]
}

/**
 * Codec definition - represents a codec family (MP3, AAC, FLAC, etc.)
 */
export interface CodecDefinition {
  codec: string // Codec identifier (MP3, AAC, FLAC, etc.)
  label: string // Display label
  isLossless: boolean
  bitrates?: number[] // Available bitrates for lossy codecs
  supportsVBR?: boolean // Whether codec supports variable bitrate
}

/**
 * Quality item for the drag-and-drop UI
 */
export interface QualityItem {
  id: string // Full quality ID (e.g., "MP3 320kbps")
  codec: string // Codec name
  bitrate?: number // Bitrate in kbps
  label: string // Display label
  isLossless: boolean
  enabled: boolean // Whether quality is selected
  priority: number // Position in list (lower = higher priority)
}

export interface QualityScore {
  searchResult: SearchResult
  totalScore: number
  scoreBreakdown: Record<string, number>
  rejectionReasons: string[]
  isRejected: boolean
  // Optional Prowlarr-style composite smart score and breakdown
  smartScore?: number
  smartScoreBreakdown?: Record<string, number>
}

export type SearchSortBy =
  | 'Seeders'
  | 'Leechers'
  | 'Size'
  | 'PublishedDate'
  | 'Title'
  | 'Source'
  | 'Language'
  | 'Quality'
  | 'Grabs'
  | 'Score'

export type SearchSortDirection = 'Ascending' | 'Descending'

// Manual import types (correspond to server ManualImport DTOs)
export interface ManualImportPreviewItem {
  relativePath: string
  fullPath: string
  size: string
  series?: string | null
  season?: string | null
  episodes?: string | null
  quality?: string | null
  languages: string[]
  releaseType?: string
}

export interface ManualImportPreviewResponse {
  items: ManualImportPreviewItem[]
}

export interface ManualImportRequestItem {
  relativePath?: string
  fullPath: string
  matchedAudiobookId?: number
  releaseGroup?: string | null
  qualityProfileId?: number | null
  language?: string | null
  size?: string | null
}

export interface ManualImportRequest {
  path: string
  mode?: 'automatic' | 'interactive'
  action?: 'none' | 'move' | 'copy' | 'hardlink/copy'
  includeCompanionFiles?: boolean
  cleanupEmptySourceFolders?: boolean
  items?: ManualImportRequestItem[]
}

export interface ManualImportResult {
  success: boolean
  sourcePath?: string
  destinationPath?: string
  audiobook?: Audiobook
  error?: string
  skipped?: boolean
  skipReason?: string
  requestedAction?: string
  effectiveAction?: string
  sourceDisposition?: string
  warningCode?: string
  warning?: string
}

// Audible API Types
export interface AudibleBookResponse {
  asin?: string
  title?: string
  subtitle?: string
  authors?: AudibleAuthor[]
  narrators?: AudibleNarrator[]
  publisher?: string
  publishDate?: string
  description?: string
  imageUrl?: string
  lengthMinutes?: number
  runtime?: number
  language?: string
  genres?: AudibleGenre[]
  series?: AudibleSeries[]
  explicit?: boolean
  releaseDate?: string
  isbn?: string
  region?: string
  bookFormat?: string
}

export interface AudibleAuthor {
  asin?: string
  name?: string
  region?: string
}

export interface AudibleNarrator {
  name?: string
}

export interface AudibleGenre {
  asin?: string
  name?: string
  type?: string
}

export interface AudibleSeries {
  asin?: string
  name?: string
  position?: string
}

export interface AudibleSearchResponse {
  results?: AudibleSearchResult[]
  totalResults?: number
}

export interface AudibleSearchResult {
  asin?: string
  title?: string
  /**
   * Audible sometimes numbers series entries here instead of the title —
   * live case: "Favorite Science Fiction Stories" volumes 3 and 8 carry
   * "Volume 3"/"Volume 8" ONLY as subtitles, making them indistinguishable
   * in a candidate list that hides this field.
   */
  subtitle?: string
  authors?: AudibleAuthor[]
  imageUrl?: string
  lengthMinutes?: number
  language?: string
  series?: AudibleSeries[]
  publisher?: string
  narrators?: AudibleNarrator[]
  releaseDate?: string
  link?: string
  /**
   * Lowercase Audible region code (`us`, `uk`, `ca`, `au`, …) that this result
   * came from, so multi-region UIs can route the subsequent per-ASIN metadata
   * lookup to the right store. Optional: the backend may not tag it.
   */
  region?: string
}

/**
 * Response wrapper for search operations that can contain different types of results
 */
export interface SearchResponse {
  indexerResults: IndexerSearchResult[]
  metadataResults: MetadataSearchResult[]
  totalCount: number
}

// Unmatched file scan types

export interface UnmatchedFileItem {
  fullPath: string
  sourceFiles?: string[]
  relativePath: string
  bookFolder: string
  size: number
  fileCount: number
  title?: string
  author?: string
  series?: string
  seriesNumber?: string
  year?: string
  narrator?: string
  description?: string
  coverPath?: string
  asin?: string
  format: string
  duration?: string
}

export interface UnmatchedFilesResponse {
  jobId: string
  status: 'Queued' | 'Processing' | 'Completed' | 'Failed'
  error?: string
  items: UnmatchedFileItem[]
}

export interface SavedUnmatchedResponse {
  lastScannedAt?: string
  items: UnmatchedFileItem[]
}

export interface BulkRenameRequest {
  audiobookIds: number[]
}

export interface FileRenamePreview {
  fileId: number
  currentPath?: string
  newPath?: string
  currentFilename?: string
  newFilename?: string
  changed: boolean
}

export interface RenamePathSemanticsSnapshot {
  syntax: 'Windows' | 'Unix'
  caseSensitivity: 'Unknown' | 'Sensitive' | 'Insensitive'
  requestedMode: 'Auto' | 'Sensitive' | 'Insensitive'
  boundaryPath: string
}

export interface RenamePreview {
  audiobookId: number
  audiobookTitle?: string
  currentFolderPath?: string
  currentFolderSemantics?: RenamePathSemanticsSnapshot
  newFolderPath?: string
  folderChanged: boolean
  fileRenames: FileRenamePreview[]
  hasChanges: boolean
}

export interface FileRenameOperation {
  fileId: number
  currentPath: string
  newPath: string
}

export interface RenameOperation {
  audiobookId: number
  currentFolderPath?: string
  currentFolderSemantics?: RenamePathSemanticsSnapshot
  newFolderPath?: string
  fileRenames: FileRenameOperation[]
}

export interface ExecuteRenameRequest {
  operations: RenameOperation[]
}

export interface FileRenameResultItem {
  fileId: number
  previousPath?: string
  newPath?: string
  success: boolean
  rolledBack: boolean
  error?: string
}

export interface RenameResult {
  audiobookId: number
  success: boolean
  conflict: boolean
  error?: string
  renamedFiles: FileRenameResultItem[]
}

export interface AuthorMonitoringExclusion {
  id: number
  title: string
  authorName?: string | null
  asin?: string | null
  createdAt: string
}

/**
 * One background automatic-search activity event. `stage` is a small vocabulary
 * the UI styles by: `searching` (a book is being queried), `grabbed` (a release
 * was queued), `no_results` (nothing found), `idle` (the sweep isn't running).
 */
export interface SearchActivityEvent {
  message: string
  stage: 'searching' | 'grabbed' | 'no_results' | 'idle' | string
  audiobookId?: number | null
  asin?: string | null
  timestamp: string
}

export interface EmbeddedFileMetadata {
  fileId: number
  audiobookId: number
  currentPath?: string
  size?: number
  title?: string
  subtitle?: string
  author?: string
  albumArtist?: string
  narrator?: string
  album?: string
  description?: string
  genre?: string
  year?: number
  asin?: string
  isbn?: string
  series?: string
  seriesPosition?: number
  durationSeconds?: number
  bitRate?: number
  format?: string
}

/** One row from GET /library/series/health — catalog-aware series completeness. */
export interface SeriesHealthApiRow {
  name: string
  owned: number
  missingTracked: number
  catalogTotal: number | null
  /** Distinct recording runs (narrations/dramatizations) in the cached catalog. */
  editions?: number | null
  monitored: boolean
  complete: boolean
  /** Owned works / catalog total (0..1); null when no catalog is cached. */
  completion?: number | null
  /** The user's most-built recording edition — the "one more grab finishes it" number. */
  bestRun?: {
    label: string
    kind: string
    narrators: string[]
    owned: number
    total: number
    completion: number
  } | null
}

/** Add-ready metadata for one missing work within a recording run. */
export interface SeriesEditionMissingEntry {
  asin: string
  title: string
  subtitle?: string | null
  authors?: string[]
  narrators?: string[]
  publisher?: string | null
  language?: string | null
  runtime?: number | null
  imageUrl?: string | null
  series?: string | null
  seriesNumber?: string | null
  publishedDate?: string | null
  isbn?: string | null
}

/** One production run of a series (heuristic grouping — see backend classifier). */
export interface SeriesEditionRun {
  label: string
  kind: string
  narrators: string[]
  publisher?: string | null
  totalWorks: number
  ownedWorks: number
  recordings: number
  missingEntries: SeriesEditionMissingEntry[]
}

export interface SeriesEditionsResponse {
  name: string
  region: string
  totalWorks: number
  ownedWorks: number
  catalogCached: boolean
  runs: SeriesEditionRun[]
}

export interface SeriesHealthResponse {
  region: string
  rows: SeriesHealthApiRow[]
}

export interface SearchActivityResponse {
  current: SearchActivityEvent | null
  recent: SearchActivityEvent[]
}

/** One row from GET /library/music-candidates — a book that "smells like music" or a TTS rip. */
export interface MusicCandidate {
  id: number
  title: string | null
  /** Which detector flagged it: a music album or a text-to-speech rip. */
  kind?: 'music' | 'tts'
  /** 0..1 detector score; >= 0.5 surfaced for review. */
  score: number
  reasons: string[]
  fileCount: number
  medianDurationSeconds: number
}

export interface MusicCandidatesResponse {
  candidates: MusicCandidate[]
}

/** GET /verification/queue-status — the dashboard's background-activity panel. */
export interface VerificationQueueStatus {
  verification: {
    queuedJobs: number
    queuedBooks: number
    hasUnknownSizedJobs: boolean
    processing: {
      jobId: string
      audiobookId?: number | null
      title?: string | null
      processed: number
      total: number
    } | null
    completedBooksToday: number
    avgSecondsPerBook: number
    etaSeconds?: number | null
  }
  seriesBackfill: {
    cached: number
    totalMultiBook: number
  }
}

export interface SeriesBackfillRunResult {
  message: string
  totalSeries: number
  alreadyCached: number
  fetched: number
  failed: number
  remaining: number
}

/**
 * One audiobook in the organize-library preview. The server has bucketed
 * each row into one of `already_canonical` / `will_move` / `collision` /
 * `invalid_target` based on the configured FolderNamingPattern.
 */
export type OrganizePreviewStatus =
  | 'already_canonical'
  | 'will_move'
  | 'collision'
  | 'invalid_target'

export interface OrganizePreviewRow {
  id: number
  title: string | null
  author: string | null
  currentPath: string | null
  targetPath: string | null
  fileCount: number
  totalSize: number
  status: OrganizePreviewStatus
  /** Set when status is `collision` — the shared normalized target. */
  collisionKey: string | null
  /** Set when status is `invalid_target` — the human-readable reason. */
  reason: string | null
  /**
   * Set when status is `invalid_target` — machine-readable companion to
   * `reason`. One of the `OrganizeInvalidReasonCode` values the backend emits
   * (`missing_title`, `missing_author`, `source_at_root`, `target_ancestor`,
   * `target_exists`, `source_missing`, …). The UI groups invalid rows by this code.
   */
  reasonCode: string | null
  /**
   * True only for `target_ancestor` rows that pass the backend's read-only
   * flatten feasibility check. The UI shows the one-click "Flatten" action only
   * when this is true.
   */
  canFlatten: boolean
  /**
   * True for `will_move` rows whose target folder exists on disk but holds only
   * leftover metadata (no audio, nothing referenced by the DB). The move will
   * replace the stub.
   */
  replacesStubTarget: boolean
}

/**
 * One row in the currently-processing / recent-completed / recent-failed
 * tails of a `GET /library/move/summary` response. Mirrors `MoveJob`
 * fields the summary endpoint projects, plus the audiobook's title and
 * per-job file count / total bytes so the UI can show size context
 * without a second round-trip.
 */
export interface MoveQueueJobSummary {
  id: string
  audiobookId: number
  audiobookTitle: string | null
  status: string
  error: string | null
  requestedPath: string | null
  sourcePath: string | null
  enqueuedAt: string
  updatedAt: string | null
  attemptCount: number
  /** Number of tracked AudiobookFile rows for the referenced audiobook. */
  fileCount: number
  /** Sum of AudiobookFile.Size (bytes) for the referenced audiobook. 0 when unknown. */
  totalBytes: number
}

/**
 * Shape returned by `GET /library/move/summary`. `total` is the sum of
 * every non-purged MoveJob row; the per-status counts always sum to it
 * (with anything unrecognized falling into `other` so the UI never
 * silently drops a count). `queuedFiles` / `queuedBytes` aggregate
 * across all Queued rows so the banner can show "queue: N files, M GB
 * remaining" for ETA context.
 */
export interface MoveQueueSummary {
  total: number
  queued: number
  processing: number
  completed: number
  failed: number
  other: number
  queuedFiles: number
  queuedBytes: number
  currentlyProcessing: MoveQueueJobSummary[]
  recentCompleted: MoveQueueJobSummary[]
  recentFailed: MoveQueueJobSummary[]
}

export interface OrganizeLibraryPreview {
  rows: OrganizePreviewRow[]
  alreadyCanonicalCount: number
  willMoveCount: number
  collisionCount: number
  invalidTargetCount: number
}

export interface OrganizeApplySkipped {
  audiobookId: number
  reason: string
}

export interface OrganizeFlattenResult {
  success: boolean
  filesMoved: number
  newPath: string | null
  error: string | null
}

export interface OrganizeQueuedJob {
  jobId: string
  audiobookId: number
  audiobookTitle: string | null
  targetPath: string | null
}

export interface OrganizeLibraryApplyResult {
  queued: number
  skipped: number
  failedToQueue: number
  queuedJobs: OrganizeQueuedJob[]
  skippedDetails: OrganizeApplySkipped[]
  warnings: string[]
}

/** One book inside a duplicate-record group from GET /library/duplicates. */
export interface DuplicateBookSummary {
  id: number
  title: string | null
  subtitle?: string | null
  publishYear?: string | null
  asin?: string | null
  fileCount: number
  fileSize: number
  monitored?: boolean
}

export interface DuplicateGroup {
  key: string
  reason: 'asin' | 'title-author'
  books: DuplicateBookSummary[]
  suggestedKeeperId: number
}

/** A record likely holding the same audio twice (two filename schemes). */
export interface DuplicateCopyBook {
  id: number
  title: string | null
  clusterCount: number
  fileCount: number
}

export interface DuplicateCopyCluster {
  key: string
  displayName: string
  fileIds: number[]
  fileCount: number
  totalBytes: number
  totalDurationSeconds: number
  onDiskCount: number
}

export interface DuplicateCopyProposal {
  keeper: DuplicateCopyCluster
  redundant: DuplicateCopyCluster[]
  confidence: 'identical' | 'high' | 'review'
  evidence: string[]
  reclaimableBytes: number
}

export interface DuplicateCopyAnalysisRecord {
  id: number
  title: string
  basePath: string | null
  fileCount: number
  splitCandidate: boolean
  proposals: DuplicateCopyProposal[]
}

export interface DuplicateCopyApplication {
  audiobookId: number
  redundantFileIds: number[]
}

export interface DuplicateCopyApplyResult {
  results: {
    audiobookId: number
    applied: boolean
    reason?: string
    confidence?: string
    filesDeleted?: number
    freedBytes?: number
    warnings?: string[]
  }[]
  totalFilesDeleted: number
  totalFreedBytes: number
}

export interface DuplicateCopyAnalysisResponse {
  records: DuplicateCopyAnalysisRecord[]
  totalReclaimableBytes: number
}

export interface LibraryDuplicatesResponse {
  duplicateGroups: DuplicateGroup[]
  duplicateCopyBooks: DuplicateCopyBook[]
}

/**
 * Wire shape for `POST /library/duplicates/merge`. Losers merge into the
 * winner (same-ASIN rows: files deleted from disk, downloads/history/move
 * jobs reassigned, rows removed); clear-ASIN ids keep their row but lose the
 * ASIN (same ASIN, actually different books).
 */
export interface DuplicatesMergePair {
  winnerId: number | null
  loserIds: number[]
  clearAsinIds: number[]
}

export interface MergeDuplicatesResult {
  groupsProcessed: number
  rowsDeleted: number
  asinsCleared: number
  downloadsReassigned: number
  historyReassigned: number
  moveJobsReassigned: number
  diskFilesDeleted: number
  diskFoldersDeleted: number
  warnings: string[]
}

/**
 * One side of a PUT /library/{id} ASIN collision — both the record being
 * edited and the record already holding the ASIN get this summary, so the
 * resolution panel can compare their relative merits (files, size, bitrate,
 * verification verdicts) instead of forcing a blind choice.
 */
export interface AsinConflictSide {
  audiobookId: number
  title: string
  authors?: string[]
  basePath?: string | null
  asin?: string | null
  fileCount: number
  totalSizeBytes: number
  maxBitrate: number
  formats: string[]
  runtime?: number | null
  verificationStatus?: string | number | null
  verificationConfidence?: number | null
}

export interface ResolveAsinConflictResult {
  winnerId: number
  loserId: number
  diskFilesDeleted: number
  diskFolderDeleted: boolean
  warnings: string[]
}

/** Result shape shared by the dry-run-first recovery endpoints. */
export interface RecoveryRunResult {
  dryRun: boolean
  inspected?: number
  recovered?: number
  skipped?: number
  [key: string]: unknown
}

export type ExtractDuplicateStrategy =
  | 'none'
  | 'merge'
  | 'duplicate'
  | 'None'
  | 'Merge'
  | 'Duplicate'

export interface ExtractFileRequest {
  metadata: AudibleBookMetadata
  duplicateStrategy?: ExtractDuplicateStrategy
  qualityProfileId?: number
  monitored?: boolean
  cacheImageLocally?: boolean
}

export interface ExtractFileConflict {
  existingAudiobookId: number
  existingTitle?: string
  existingAsin?: string
  existingFileCount: number
  /** Folder the existing (conflicting) audiobook lives in on disk. */
  existingBasePath?: string
  /** Folder a new audiobook would land in if the user picks Duplicate — derived from
   *  the chosen Audible metadata. */
  proposedDestinationFolder?: string
  recommendedStrategy: 'merge' | 'duplicate' | 'Merge' | 'Duplicate'
  recommendationReason?: string
  /** A capped sample of the existing audiobook's tracked files (path / format / size /
   *  duration) so the UI can show "what's already there" when the user is choosing
   *  between merge and duplicate. */
  existingFiles?: ExtractFileConflictExistingFile[]
}

export interface ExtractFileConflictExistingFile {
  fileId: number
  path?: string
  format?: string
  size?: number
  durationSeconds?: number
}

export interface ExtractFileResult {
  success: boolean
  error?: string
  appliedStrategy: ExtractDuplicateStrategy
  destinationAudiobookId?: number
  destinationAudiobookTitle?: string
  newFilePath?: string
  sourceAudiobookId: number
  sourceAudiobookEmpty: boolean
  conflict?: ExtractFileConflict
}
