import { EmployeesSection } from '../components/EmployeesSection';
import { SettingsNav } from '../components/SettingsNav';

export default function SettingsEmployeesPage() {
  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <h1 className="text-xl font-semibold">Settings</h1>
      <SettingsNav />
      <EmployeesSection />
    </div>
  );
}
