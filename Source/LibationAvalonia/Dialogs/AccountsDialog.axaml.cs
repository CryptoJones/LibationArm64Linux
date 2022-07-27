using AudibleUtilities;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using AudibleApi;

namespace LibationAvalonia.Dialogs
{
	public partial class AccountsDialog : DialogWindow
	{
		public ObservableCollection<AccountDto> Accounts { get; } = new();
		public class AccountDto : ViewModels.ViewModelBase
		{
			private string _accountId;
			private Locale _selectedLocale;
			public IReadOnlyList<Locale> Locales => AccountsDialog.Locales;
			public bool LibraryScan { get; set; } = true;
			public string AccountId
			{
				get => _accountId;
				set
				{
					this.RaiseAndSetIfChanged(ref _accountId, value);
					this.RaisePropertyChanged(nameof(IsDefault));
				} 
			}
			public Locale SelectedLocale
			{
				get => _selectedLocale;
				set
				{
					this.RaiseAndSetIfChanged(ref _selectedLocale, value);
					this.RaisePropertyChanged(nameof(IsDefault));
				}
			}
			public string AccountName { get; set; }
			public bool IsDefault => string.IsNullOrEmpty(AccountId) && SelectedLocale is null;
		}

		private static string GetAudibleCliAppDataPath()
			=> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Audible");

		private static IReadOnlyList<Locale> Locales => Localization.Locales.OrderBy(l => l.Name).ToList();
		public AccountsDialog()
		{
			InitializeComponent();

			// WARNING: accounts persister will write ANY EDIT to object immediately to file
			// here: copy strings and dispose of persister
			// only persist in 'save' step
			using var persister = AudibleApiStorage.GetAccountsSettingsPersister();
			var accounts = persister.AccountsSettings.Accounts;
			if (accounts.Any())
			{
				foreach (var account in accounts)
					AddAccountToGrid(account);
			}

			DataContext = this;
			addBlankAccount();
		}
		private void addBlankAccount()
		{

			var newBlank = new AccountDto();
			newBlank.PropertyChanged += AccountDto_PropertyChanged;
			Accounts.Insert(Accounts.Count, newBlank);
		}

		private void AddAccountToGrid(Account account)
		{
			AccountDto accountDto = new()
			{
				LibraryScan = account.LibraryScan,
				AccountId = account.AccountId,
				SelectedLocale = Locales.Single(l => l.Name == account.Locale.Name),
				AccountName = account.AccountName,
			};
			accountDto.PropertyChanged += AccountDto_PropertyChanged;

			//ObservableCollection doesn't fire CollectionChanged on Add, so use Insert instead
			Accounts.Insert(Accounts.Count, accountDto);
		}

		private void AccountDto_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (Accounts.Any(a => a.IsDefault))
				return;

			addBlankAccount();
		}

		public void DeleteButton_Clicked(object sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			if (e.Source is Button expBtn && expBtn.DataContext is AccountDto acc)
			{
				var index = Accounts.IndexOf(acc);
				if (index < 0) return;

				acc.PropertyChanged -= AccountDto_PropertyChanged;
				Accounts.Remove(acc);
			}
		}

		public async void ImportButton_Clicked(object sender, Avalonia.Interactivity.RoutedEventArgs e)
		{

			OpenFileDialog ofd = new();
			ofd.Filters.Add(new() { Name = "JSON File", Extensions = new() { "json" } });
			ofd.Directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			ofd.AllowMultiple = false;

			string audibleAppDataDir = GetAudibleCliAppDataPath();

			if (Directory.Exists(audibleAppDataDir))
				ofd.Directory = audibleAppDataDir;

			var filePath = await ofd.ShowAsync(this);

			if (filePath is null || filePath.Length == 0) return;

			try
			{
				var jsonText = File.ReadAllText(filePath[0]);
				var mkbAuth = Mkb79Auth.FromJson(jsonText);
				var account = await mkbAuth.ToAccountAsync();

				// without transaction, accounts persister will write ANY EDIT immediately to file
				using var persister = AudibleApiStorage.GetAccountsSettingsPersister();

				if (persister.AccountsSettings.Accounts.Any(a => a.AccountId == account.AccountId && a.IdentityTokens.Locale.Name == account.Locale.Name))
				{
					MessageBox.Show(this, $"An account with that account id and country already exists.\r\n\r\nAccount ID: {account.AccountId}\r\nCountry: {account.Locale.Name}", "Cannot Add Duplicate Account");
					return;
				}

				persister.AccountsSettings.Add(account);

				AddAccountToGrid(account);
			}
			catch (Exception ex)
			{
				MessageBox.ShowAdminAlert(
						this,
						$"An error occurred while importing an account from:\r\n{filePath[0]}\r\n\r\nIs the file encrypted?",
						"Error Importing Account",
						ex);
			}
		}

		public void ExportButton_Clicked(object sender, Avalonia.Interactivity.RoutedEventArgs e)
		{
			if (e.Source is Button expBtn && expBtn.DataContext is AccountDto acc)
				Export(acc);
		}

		protected override void SaveAndClose()
		{
			try
			{
				if (!inputIsValid())
					return;

				// without transaction, accounts persister will write ANY EDIT immediately to file
				using var persister = AudibleApiStorage.GetAccountsSettingsPersister();

				persister.BeginTransation();
				persist(persister.AccountsSettings);
				persister.CommitTransation();

				base.SaveAndClose();
			}
			catch (Exception ex)
			{
				MessageBox.ShowAdminAlert(this, "Error attempting to save accounts", "Error saving accounts", ex);
			}
		}

		public async void SaveButton_Clicked(object sender, Avalonia.Interactivity.RoutedEventArgs e)
			=> await SaveAndCloseAsync();


		private void InitializeComponent()
		{
			AvaloniaXamlLoader.Load(this);
		}

		private void persist(AccountsSettings accountsSettings)
		{
			var existingAccounts = accountsSettings.Accounts;

			// editing account id is a special case. an account is defined by its account id, therefore this is really a different account. the user won't care about this distinction though.
			// these will be caught below by normal means and re-created minus the convenience of persisting identity tokens

			// delete
			for (var i = existingAccounts.Count - 1; i >= 0; i--)
			{
				var existing = existingAccounts[i];
				if (!Accounts.Any(dto =>
					dto.AccountId?.ToLower().Trim() == existing.AccountId.ToLower()
					&& dto.SelectedLocale?.Name == existing.Locale?.Name))
				{
					accountsSettings.Delete(existing);
				}
			}

			// upsert each. validation occurs through Account and AccountsSettings
			foreach (var dto in Accounts)
			{
				var acct = accountsSettings.Upsert(dto.AccountId, dto.SelectedLocale?.Name);
				acct.LibraryScan = dto.LibraryScan;
				acct.AccountName
					= string.IsNullOrWhiteSpace(dto.AccountName)
					? $"{dto.AccountId} - {dto.SelectedLocale?.Name}"
					: dto.AccountName.Trim();
			}
		}
		private bool inputIsValid()
		{
			foreach (var dto in Accounts.ToList())
			{
				if (dto.IsDefault)
				{
					Accounts.Remove(dto);
					continue;
				}

				if (string.IsNullOrWhiteSpace(dto.AccountId))
				{
					MessageBox.Show(this, "Account id cannot be blank. Please enter an account id for all accounts.", "Blank account", MessageBoxButtons.OK, MessageBoxIcon.Error);
					return false;
				}

				if (string.IsNullOrWhiteSpace(dto.SelectedLocale?.Name))
				{
					MessageBox.Show(this, "Please select a locale (i.e.: country or region) for all accounts.", "Blank region", MessageBoxButtons.OK, MessageBoxIcon.Error);
					return false;
				}
			}

			return true;
		}

		private async void Export(AccountDto acc)
		{
			// without transaction, accounts persister will write ANY EDIT immediately to file
			using var persister = AudibleApiStorage.GetAccountsSettingsPersister();

			var account = persister.AccountsSettings.Accounts.FirstOrDefault(a => a.AccountId == acc.AccountId && a.Locale.Name == acc.SelectedLocale?.Name);

			if (account is null)
				return;

			if (account.IdentityTokens?.IsValid != true)
			{
				MessageBox.Show(this, "This account hasn't been authenticated yet. First scan your library to log into your account, then try exporting again.", "Account Not Authenticated");
				return;
			}

			SaveFileDialog sfd = new();
			sfd.Filters.Add(new() { Name = "JSON File", Extensions = new() { "json" } });

			string audibleAppDataDir = GetAudibleCliAppDataPath();

			if (Directory.Exists(audibleAppDataDir))
				sfd.Directory = audibleAppDataDir;

			string fileName = await sfd.ShowAsync(this);
			if (fileName is null)
				return;

			try
			{
				var mkbAuth = Mkb79Auth.FromAccount(account);
				var jsonText = mkbAuth.ToJson();

				File.WriteAllText(fileName, jsonText);

				MessageBox.Show(this, $"Successfully exported {account.AccountName} to\r\n\r\n{fileName}", "Success!");
			}
			catch (Exception ex)
			{
				MessageBox.ShowAdminAlert(
					this,
					$"An error occurred while exporting account:\r\n{account.AccountName}",
					"Error Exporting Account",
					ex);
			}
		}
	}
}