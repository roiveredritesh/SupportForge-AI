export function ConfidenceBadge({ confidence }: { confidence: number }) {
  const label = confidence >= 0.7 ? 'High confidence' : confidence >= 0.4 ? 'Medium confidence' : 'Low confidence';
  const color = confidence >= 0.7 ? 'bg-green-100 text-green-800' : confidence >= 0.4 ? 'bg-yellow-100 text-yellow-800' : 'bg-red-100 text-red-800';

  return <span className={`text-xs px-2 py-1 rounded ${color}`}>{label}</span>;
}
