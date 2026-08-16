import { FeedbackDashboardSection } from '../components/FeedbackDashboardSection';
import { SettingsNav } from '../components/SettingsNav';

export default function SettingsFeedbackPage() {
  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <h1 className="text-xl font-semibold">Settings</h1>
      <SettingsNav />
      <FeedbackDashboardSection />
    </div>
  );
}
