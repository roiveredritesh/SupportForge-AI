import { ConnectedAppsSection } from '../components/ConnectedAppsSection';
import { SettingsNav } from '../components/SettingsNav';

export default function SettingsConnectedAppsPage() {
  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <h1 className="text-xl font-semibold">Settings</h1>
      <SettingsNav />
      <ConnectedAppsSection />
    </div>
  );
}
