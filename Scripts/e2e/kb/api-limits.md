# API rate limits

The public API allows 100 requests per minute per API key. Requests above the limit receive HTTP 429 with a Retry-After header. Webhook deliveries are retried up to 5 times with exponential backoff.
